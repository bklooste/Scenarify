using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace XUnitServiceBusTests;

[CollectionDefinition(Name)]
public sealed class ContainerCollection : ICollectionFixture<Containers>
{
    public const string Name = "containers";
}

/// <summary>
/// The real Azure Service Bus emulator (backed by Azure SQL Edge on a private network) and MockServer, which doubles as
/// "the service under test" so the fixture's health wait runs against a real server. Entities come from emulator/Config.json.
/// </summary>
public sealed class Containers : IAsyncLifetime
{
    public const string Orders = "orders";
    public const string OrderEvents = "order-events";
    public const string OrderEventsSubscription = "order-events-sub";

    private const string SqlAlias = "sqledge";
    private const string SaPassword = "Scenarify!Passw0rd";

    private readonly INetwork network = new NetworkBuilder().WithName($"scenarify-sb-{Guid.NewGuid():N}").Build();
    private readonly IContainer sql;
    private readonly IContainer emulator;

    private readonly IContainer mockServer = new ContainerBuilder("mockserver/mockserver:5.15.0")
        .WithCommand("-serverPort", "1090", "-logLevel", "WARN")
        .WithPortBinding(1090, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(1090))
        .Build();

    public Containers()
    {
        sql = new ContainerBuilder("mcr.microsoft.com/azure-sql-edge:latest")
            .WithNetwork(network)
            .WithNetworkAliases(SqlAlias)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("MSSQL_SA_PASSWORD", SaPassword)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("SQL Server is now ready for client connections"))
            .Build();

        emulator = new ContainerBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
            .WithNetwork(network)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("SQL_SERVER", SqlAlias)
            .WithEnvironment("MSSQL_SA_PASSWORD", SaPassword)
            .WithResourceMapping(new FileInfo(Path.Combine(AppContext.BaseDirectory, "emulator", "Config.json")), "/ServiceBus_Emulator/ConfigFiles/")
            .WithPortBinding(5672, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Emulator Service is Successfully Up!"))
            .Build();
    }

    public string ConnectionString { get; private set; } = "";

    public string MockUrl { get; private set; } = "";

    public MockBackedFixture Fixture { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await network.CreateAsync();
        await Task.WhenAll(sql.StartAsync(), mockServer.StartAsync());
        await emulator.StartAsync();
        ConnectionString = ServiceBusFeature.DefaultConnectionString.Replace(
            "sb://localhost;", $"sb://{emulator.Hostname}:{emulator.GetMappedPublicPort(5672)};", StringComparison.Ordinal);
        MockUrl = $"http://{mockServer.Hostname}:{mockServer.GetMappedPublicPort(1090)}";
        Fixture = new MockBackedFixture(MockUrl, ConnectionString);
        await Fixture.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Fixture is not null)
            await Fixture.DisposeAsync();
        await Task.WhenAll(emulator.DisposeAsync().AsTask(), mockServer.DisposeAsync().AsTask());
        await sql.DisposeAsync();
        await network.DisposeAsync();
    }
}

public sealed class MockBackedFixture(string mockUrl, string connectionString) : ServiceTestFixture(new ServiceTestOptions
{
    MockServer = true,
    MockServerFiles = ["mockserver/health.json"],
    Brand = "sbx",
    Config = new Dictionary<string, string?>
    {
        [BaseUrlKey] = mockUrl,
        [MockServerUrlKey] = mockUrl,
        ["ServiceBus:ConnectionString"] = connectionString,
    },
    Features =
    [
        new ServiceBusFeature(
            publish: [Containers.Orders, Containers.OrderEvents],
            record: [Containers.Orders, Containers.OrderEvents],
            subscriptions: new Dictionary<string, string> { [Containers.OrderEvents] = Containers.OrderEventsSubscription }),
    ],
});

public sealed record OrderPlaced(string OrderId, string CustomerId, decimal Amount, string[] Items);
