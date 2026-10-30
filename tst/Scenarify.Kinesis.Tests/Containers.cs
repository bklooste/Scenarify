using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace XUnitKinesisTests;

[CollectionDefinition(Name)]
public sealed class ContainerCollection : ICollectionFixture<Containers>
{
    public const string Name = "containers";
}

/// <summary>
/// Real Kinesis (LocalStack) and MockServer. MockServer doubles as "the service under test", so the fixture's
/// health wait runs against a real server.
/// </summary>
public sealed class Containers : IAsyncLifetime
{
    public const string Orders = "orders";
    public const string Keyed = "orders-keyed";

    private readonly IContainer localStack = new ContainerBuilder("localstack/localstack:3")
        .WithEnvironment("SERVICES", "kinesis")
        .WithPortBinding(4566, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
            .ForPort(4566).ForPath("/_localstack/health")))
        .Build();

    private readonly IContainer mockServer = new ContainerBuilder("mockserver/mockserver:5.15.0")
        .WithCommand("-serverPort", "1090", "-logLevel", "WARN")
        .WithPortBinding(1090, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(1090))
        .Build();

    public string KinesisUrl { get; private set; } = "";

    public string MockUrl { get; private set; } = "";

    public MockBackedFixture Fixture { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(localStack.StartAsync(), mockServer.StartAsync());
        KinesisUrl = $"http://{localStack.Hostname}:{localStack.GetMappedPublicPort(4566)}";
        MockUrl = $"http://{mockServer.Hostname}:{mockServer.GetMappedPublicPort(1090)}";
        Fixture = new MockBackedFixture(MockUrl, KinesisUrl);
        await Fixture.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Fixture.DisposeAsync();
        await Task.WhenAll(localStack.DisposeAsync().AsTask(), mockServer.DisposeAsync().AsTask());
    }
}

public sealed class MockBackedFixture(string mockUrl, string kinesisUrl, IEnumerable<string>? extraStreams = null) : ServiceTestFixture(new ServiceTestOptions
{
    MockServer = true,
    MockServerFiles = ["mockserver/health.json"],
    Brand = "sbx",
    Config = new Dictionary<string, string?>
    {
        [BaseUrlKey] = mockUrl,
        [MockServerUrlKey] = mockUrl,
        ["Kinesis:ServiceUrl"] = kinesisUrl,
    },
    Features =
    [
        new KinesisFeature(
            publish: [Containers.Orders, Containers.Keyed, .. extraStreams ?? []],
            record: [Containers.Orders, Containers.Keyed]),
    ],
});

public sealed record OrderPlaced(string OrderId, string CustomerId, decimal Amount, string[] Items);
