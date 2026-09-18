using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace XUnitRedisTests;

[CollectionDefinition(Name)]
public sealed class ContainerCollection : ICollectionFixture<Containers>
{
    public const string Name = "containers";
}

/// <summary>
/// Real Redis and MockServer. MockServer doubles as "the service under test", so the fixture's HTTP steps,
/// headers and health wait run against a real server.
/// </summary>
public sealed class Containers : IAsyncLifetime
{
    public const string Orders = "orders";
    public const string TwoPartitions = "orders-2p";

    private readonly IContainer redis = new ContainerBuilder("redis:8-alpine")
        .WithPortBinding(6379, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379))
        .Build();

    private readonly IContainer mockServer = new ContainerBuilder("ghcr.io/bklooste/mockserver:mockserver-5.15.0")
        .WithCommand("-serverPort", "1090", "-logLevel", "WARN")
        .WithPortBinding(1090, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(1090))
        .Build();

    public string RedisConnection { get; private set; } = "";

    public string MockUrl { get; private set; } = "";

    public MockBackedFixture Fixture { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(redis.StartAsync(), mockServer.StartAsync());
        RedisConnection = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}";
        MockUrl = $"http://{mockServer.Hostname}:{mockServer.GetMappedPublicPort(1090)}";
        Fixture = new MockBackedFixture(MockUrl, RedisConnection);
        await Fixture.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Fixture.DisposeAsync();
        await Task.WhenAll(redis.DisposeAsync().AsTask(), mockServer.DisposeAsync().AsTask());
    }
}

// Shares the collection's MockServer: its startup reset clears expectations, so it reloads the shared health file.
public sealed class GatewayStyleFixture(string mockUrl) : ServiceTestFixture(new ServiceTestOptions
{
    MockServer = true,
    MockServerFiles = ["mockserver/health.json"],
    DefaultScopes = "ignored-scope",
    DefaultHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer e2e-token" },
    SendIdentityHeaders = false,
    Config = new Dictionary<string, string?> { [BaseUrlKey] = mockUrl, [MockServerUrlKey] = mockUrl },
});

public sealed class MockBackedFixture(string mockUrl, string redis, string? keyPrefix = null) : ServiceTestFixture(new ServiceTestOptions
{
    MockServer = true,
    MockServerFiles = ["mockserver/health.json"],
    DefaultScopes = "orders-r orders-w:{{brand}}",
    Brand = "sbx",
    Config = new Dictionary<string, string?>
    {
        [BaseUrlKey] = mockUrl,
        [MockServerUrlKey] = mockUrl,
        ["OtherHttpUrl"] = mockUrl + "/other",
    },
    Services = new Dictionary<string, string> { ["other"] = "OtherHttpUrl" },
    Features =
    [
        new RedisFeature(
            publish: [Containers.Orders, Containers.TwoPartitions],
            record: [Containers.Orders, Containers.TwoPartitions],
            partitions: new Dictionary<string, int> { [Containers.TwoPartitions] = 2 },
            connectionString: redis,
            keyPrefix: keyPrefix),
    ],
});

public sealed record OrderPlaced(string OrderId, string CustomerId, decimal Amount, string[] Items);
