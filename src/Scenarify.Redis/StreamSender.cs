using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RedisEvents.Config;
using RedisEvents.Extensions;
using RedisEvents.Producer;

namespace Scenarify;

/// <summary>
/// Publishes real messages onto Redis streams from a test, standing in for an upstream service.
/// </summary>
/// <remarks>
/// <para>The in-memory RedisEvents doubles cannot feed a service in another container, and the only public
/// way to get a real publisher is <c>AddStreamPublisher</c>, so this runs a throwaway host holding only publishers.</para>
/// <para>Partition counts must match the service's or keys hash to different streams and the service sees
/// nothing. No topic options are set unless <c>partitions</c> names the topic, so both sides share the library
/// default. Topic names may contain ':' (e.g. <c>bet:core-es</c>).</para>
/// </remarks>
public sealed class StreamSender : IAsyncDisposable
{
    private readonly IHost host;
    private readonly HashSet<string> topics;

    private StreamSender(IHost host, IEnumerable<string> topics)
    {
        this.host = host;
        this.topics = new HashSet<string>(topics, StringComparer.Ordinal);
    }

    /// <summary>
    /// Wire JSON for typed messages: web defaults, enums as strings, defaults omitted. Deliberately not
    /// <c>EventMsgHelper.JsonOptions</c>, whose <c>IgnoreReadOnlyProperties</c> serializes anonymous objects to <c>{}</c>.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>Starts a sender for <paramref name="topics"/>.</summary>
    public static async Task<StreamSender> StartAsync(string connectionString, IEnumerable<string> topics,
        IReadOnlyDictionary<string, int>? partitions = null, CancellationToken ct = default)
    {
        var list = topics.ToList();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        StreamConfig.Configure(builder, connectionString, list, partitions);
        foreach (var topic in list)
            builder.AddStreamPublisher(topic);

        var host = builder.Build();
        await host.StartAsync(ct);
        return new StreamSender(host, list);
    }

    /// <summary>Publishes <paramref name="message"/> with its CLR full name as the message type.</summary>
    public Task PublishAsync<T>(string topic, T message, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), JsonOptions);
        return PublishAsync(topic, body, TypeName(message.GetType()), partitionKey, correlationId, ct, headers);
    }

    /// <summary>Publishes raw JSON text with an explicit message type.</summary>
    public Task PublishJsonAsync(string topic, string json, string type, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null) =>
        PublishAsync(topic, Encoding.UTF8.GetBytes(json), type, partitionKey, correlationId, ct, headers);

    /// <summary>Publishes a raw body with an explicit message type.</summary>
    /// <remarks><paramref name="headers"/> are message headers (some services read settings such as a delay from them).</remarks>
    public async Task PublishAsync(string topic, ReadOnlyMemory<byte> body, string type, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (!topics.Contains(topic))
            throw new InvalidOperationException($"Topic '{topic}' is not configured for publishing. Configured: {string.Join(", ", topics)}.");

        var publisher = host.Services.GetRequiredKeyedService<IStreamPublisher>(topic);
        await publisher.PublishAsync(
            partitionKey ?? Guid.NewGuid().ToString(),
            body,
            type,
            new PublishOptions(CorrelationId: correlationId, Headers: headers),
            ct);
    }

    /// <summary>The message type string for <paramref name="type"/>: its full name, rejecting anonymous types.</summary>
    public static string TypeName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Name.Contains("AnonymousType", StringComparison.Ordinal))
            throw new ArgumentException("Anonymous objects have no stable message type; pass the type string explicitly.", nameof(type));
        return type.FullName!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>
/// Stream wiring shared by <see cref="StreamSender"/> and <see cref="StreamRecorder"/>.
/// </summary>
/// <remarks>
/// <para>Per-topic options are never written as <c>Streams:Topics:{topic}</c> configuration keys: .NET configuration
/// splits keys on ':', so a topic such as <c>bet:core-es</c> would bind as a nested section and its partition count
/// would be silently lost (the topic would run on one partition while the service under test runs on more).</para>
/// <para>Instead they go straight into the <see cref="StreamOptions"/> RedisEvents binds once per builder. A publisher
/// captures its topic's options when <c>AddStreamPublisher</c> runs, so the options must be in place before any topic
/// is registered: <see cref="Configure"/> makes RedisEvents bind them first, through an inert state-store
/// registration that is never resolved (no Redis call, no hosted service), and then fills in the topics.</para>
/// </remarks>
internal static class StreamConfig
{
    /// <summary>The topic of the inert registration that makes RedisEvents bind its options. Never resolved.</summary>
    internal const string BindTopic = "scenarify-options-bind";

    /// <summary>
    /// Adds <paramref name="settings"/> plus the connection string to <paramref name="builder"/>'s configuration,
    /// binds RedisEvents' options and sets each partitioned topic's partition count on them, by exact topic name.
    /// </summary>
    public static StreamOptions Configure(IHostApplicationBuilder builder, string connectionString, IEnumerable<string> topics,
        IReadOnlyDictionary<string, int>? partitions, IDictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase)
        {
            ["Streams:ConnectionString"] = connectionString,
        };
        builder.Configuration.AddInMemoryCollection(all);

        builder.AddStreamStore(BindTopic);
        var options = builder.Services.LastOrDefault(d => d.ServiceType == typeof(StreamOptions) && !d.IsKeyedService)?.ImplementationInstance as StreamOptions
            ?? throw new InvalidOperationException("RedisEvents did not register its bound StreamOptions; this RedisEvents version is not supported by Scenarify.Redis.");

        foreach (var topic in topics)
        {
            if (partitions is null || !partitions.TryGetValue(topic, out var count))
                continue;
            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(partitions), count, $"Partitions for topic '{topic}' must be >= 1.");
            var current = options.Topics.TryGetValue(topic, out var existing) ? existing : new TopicOptions();
            options.Topics[topic] = current with { Partitions = count };
        }
        return options;
    }
}
