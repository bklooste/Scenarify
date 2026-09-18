using StackExchange.Redis;

namespace Scenarify;

/// <summary>
/// Fixture feature for services that use Redis streams: a <see cref="StreamSender"/> for the topics the
/// test publishes to, a <see cref="StreamRecorder"/> for the topics it observes, and a Redis connection for
/// the (discouraged) seeding steps. Both hosts start before the service health wait.
/// </summary>
/// <param name="publish">Topics the tests publish to.</param>
/// <param name="record">Topics the tests observe.</param>
/// <param name="partitions">Partition counts for topics the service configures away from the library default.</param>
/// <param name="connectionString">Overrides <c>Streams:ConnectionString</c> from the fixture configuration.</param>
/// <param name="keyPrefix">Optional prefix put in front of every <c>Redis.*</c> step key (tokens expand), e.g. <c>local:</c>.</param>
/// <param name="seedTtl">Expiry applied to data the <c>Redis.*</c> steps write, unless a step passes its own; default 10 minutes.</param>
public sealed class RedisFeature(
    IEnumerable<string>? publish = null,
    IEnumerable<string>? record = null,
    IReadOnlyDictionary<string, int>? partitions = null,
    string? connectionString = null,
    string? keyPrefix = null,
    TimeSpan? seedTtl = null) : IFixtureFeature
{
    /// <summary>Default expiry for seeded test data.</summary>
    public static readonly TimeSpan DefaultSeedTtl = TimeSpan.FromMinutes(10);

    /// <summary>The prefix put in front of every <c>Redis.*</c> step key; empty for none.</summary>
    public string KeyPrefix { get; } = keyPrefix ?? "";

    /// <summary>Expiry applied to seeded data unless a step overrides it.</summary>
    public TimeSpan SeedTtl { get; } = seedTtl ?? DefaultSeedTtl;

    /// <summary>Default connection string when configuration has none.</summary>
    public const string DefaultConnectionString = "test-redis:6379";

    private readonly List<string> publishTopics = publish?.ToList() ?? [];
    private readonly List<string> recordTopics = record?.ToList() ?? [];
    private StreamSender? sender;
    private StreamRecorder? recorder;
    private Lazy<Task<ConnectionMultiplexer>>? connection;

    /// <summary>The resolved connection string (available after start).</summary>
    public string ConnectionString { get; private set; } = connectionString ?? "";

    /// <summary>The sender; throws when no publish topics were configured.</summary>
    public StreamSender Sender =>
        sender ?? throw new InvalidOperationException("RedisFeature has no publish topics: pass publish: [\"topic\"].");

    /// <summary>The recorder; throws when no record topics were configured.</summary>
    public StreamRecorder Recorder =>
        recorder ?? throw new InvalidOperationException("RedisFeature records no topics: pass record: [\"topic\"].");

    /// <summary>A Redis database for seeding. Prefer the service's API or events; say why in a comment when you use this.</summary>
    public async Task<IDatabase> DatabaseAsync() =>
        (await (connection ?? throw new InvalidOperationException("RedisFeature has not started.")).Value).GetDatabase();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<string>> channels = new(StringComparer.Ordinal);

    /// <summary>
    /// Subscribes to pub/sub <paramref name="channel"/> (idempotent) and buffers every message from now on;
    /// read them with <see cref="ChannelMessages"/>.
    /// </summary>
    public async Task SubscribeAsync(string channel)
    {
        if (channels.ContainsKey(channel))
            return;
        var buffer = channels.GetOrAdd(channel, _ => new System.Collections.Concurrent.ConcurrentQueue<string>());
        var multiplexer = await (connection ?? throw new InvalidOperationException("RedisFeature has not started.")).Value;
        await multiplexer.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, message) => buffer.Enqueue(message.ToString()));
    }

    /// <summary>Messages received so far on a subscribed <paramref name="channel"/>.</summary>
    public IReadOnlyList<string> ChannelMessages(string channel) =>
        channels.TryGetValue(channel, out var buffer)
            ? [.. buffer]
            : throw new InvalidOperationException($"Channel '{channel}' is not subscribed: add .Given(Redis.Subscribe(...)) first.");

    /// <inheritdoc />
    public async ValueTask StartAsync(ServiceTestFixture fixture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (ConnectionString.Length == 0)
        {
            ConnectionString = fixture.Config["Streams:ConnectionString"]
                ?? fixture.Config["ConnectionStrings:Redis"]
                ?? DefaultConnectionString;
        }

        connection = new(() => ConnectionMultiplexer.ConnectAsync(ConnectionString));
        if (publishTopics.Count > 0)
            sender = await StreamSender.StartAsync(ConnectionString, publishTopics, partitions, ct);
        if (recordTopics.Count > 0)
            recorder = await StreamRecorder.StartAsync(ConnectionString, recordTopics, partitions, ct);
        fixture.Log($"Redis streams ready on {ConnectionString}: publish [{string.Join(", ", publishTopics)}], record [{string.Join(", ", recordTopics)}]");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (recorder is not null)
            await recorder.DisposeAsync();
        if (sender is not null)
            await sender.DisposeAsync();
        if (connection is { IsValueCreated: true })
            await (await connection.Value).DisposeAsync();
    }
}
