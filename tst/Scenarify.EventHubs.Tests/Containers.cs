using System.Text;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace XUnitEventHubsTests;

[CollectionDefinition(Name)]
public sealed class ContainerCollection : ICollectionFixture<Containers>
{
    public const string Name = "containers";
}

/// <summary>
/// The real Azure Event Hubs emulator (with the Azurite it needs for blob and metadata storage) and MockServer.
/// MockServer doubles as "the service under test", so the fixture's HTTP steps and health wait run against a real server.
/// </summary>
public sealed class Containers : IAsyncLifetime
{
    public const string Orders = "orders";
    public const string TwoPartitions = "orders-2p";
    public const string RecorderGroup = "recorder";

    private const string AzuriteAlias = "azurite";

    // Hubs are fixed by this file: the emulator cannot create entities at runtime.
    private const string EmulatorConfig = $$"""
        {
          "UserConfig": {
            "NamespaceConfig": [
              {
                "Type": "EventHub",
                "Name": "emulatorNs1",
                "Entities": [
                  { "Name": "{{Orders}}", "PartitionCount": "1", "ConsumerGroups": [ { "Name": "{{RecorderGroup}}" } ] },
                  { "Name": "{{TwoPartitions}}", "PartitionCount": "2", "ConsumerGroups": [ { "Name": "{{RecorderGroup}}" } ] }
                ]
              }
            ],
            "LoggingConfig": { "Type": "File" }
          }
        }
        """;

    private readonly INetwork network = new NetworkBuilder().WithName($"eventhubs-{Guid.NewGuid():N}").Build();
    private readonly IContainer azurite;
    private readonly IContainer emulator;

    private readonly IContainer mockServer = new ContainerBuilder("mockserver/mockserver:5.15.0")
        .WithCommand("-serverPort", "1090", "-logLevel", "WARN")
        .WithPortBinding(1090, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(1090))
        .Build();

    public Containers()
    {
        azurite = new ContainerBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithCommand("azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--skipApiVersionCheck", "--loose")
            .WithNetwork(network)
            .WithNetworkAliases(AzuriteAlias)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Azurite Table service is successfully listening"))
            .Build();
        emulator = new ContainerBuilder("mcr.microsoft.com/azure-messaging/eventhubs-emulator:latest")
            .WithNetwork(network)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("BLOB_SERVER", AzuriteAlias)
            .WithEnvironment("METADATA_SERVER", AzuriteAlias)
            .WithResourceMapping(Encoding.UTF8.GetBytes(EmulatorConfig), "/Eventhubs_Emulator/ConfigFiles/Config.json")
            .WithPortBinding(5672, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Emulator Service is Successfully Up!"))
            .Build();
    }

    public string EventHubsConnection { get; private set; } = "";

    public string MockUrl { get; private set; } = "";

    public MockBackedFixture Fixture { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await network.CreateAsync();
        await azurite.StartAsync();
        await Task.WhenAll(emulator.StartAsync(), mockServer.StartAsync());
        EventHubsConnection = EventHubsFeature.DefaultConnectionString
            .Replace("sb://localhost", $"sb://{emulator.Hostname}:{emulator.GetMappedPublicPort(5672)}", StringComparison.Ordinal);
        MockUrl = $"http://{mockServer.Hostname}:{mockServer.GetMappedPublicPort(1090)}";
        Fixture = new MockBackedFixture(MockUrl, EventHubsConnection);
        await Fixture.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Fixture is not null)
            await Fixture.DisposeAsync();
        await Task.WhenAll(emulator.DisposeAsync().AsTask(), mockServer.DisposeAsync().AsTask());
        await azurite.DisposeAsync();
        await network.DisposeAsync();
    }
}

public sealed class MockBackedFixture(string mockUrl, string eventHubs) : ServiceTestFixture(new ServiceTestOptions
{
    MockServer = true,
    MockServerFiles = ["mockserver/health.json"],
    DefaultScopes = "orders-r orders-w:{{brand}}",
    Brand = "sbx",
    Config = new Dictionary<string, string?>
    {
        [BaseUrlKey] = mockUrl,
        [MockServerUrlKey] = mockUrl,
        ["EventHubs:ConnectionString"] = eventHubs,
    },
    Features =
    [
        // Connection string comes from config, exercising the feature's lookup.
        new EventHubsFeature(
            publish: [Containers.Orders, Containers.TwoPartitions],
            record: [Containers.Orders, Containers.TwoPartitions],
            consumerGroup: Containers.RecorderGroup),
    ],
});

public sealed record OrderPlaced(string OrderId, string CustomerId, decimal Amount, string[] Items);
