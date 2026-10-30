using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Confluent.Kafka;

namespace Scenarify;

/// <summary>Produces real Kafka records from a test, standing in for an upstream service.</summary>
/// <remarks>
/// Kafka records have no type or correlation id, so they travel as the <see cref="TypeHeader"/> and
/// <see cref="CorrelationIdHeader"/> UTF-8 headers; the partition key is the record key. Every produce awaits
/// the broker's acknowledgement, so a failure surfaces in the test that caused it.
/// </remarks>
public sealed class KafkaSender : IAsyncDisposable
{
    /// <summary>Header carrying the message type.</summary>
    public const string TypeHeader = "type";

    /// <summary>Header carrying the correlation id.</summary>
    public const string CorrelationIdHeader = "correlationId";

    private readonly IProducer<string, byte[]> producer;
    private readonly HashSet<string> topics;

    private KafkaSender(IProducer<string, byte[]> producer, IEnumerable<string> topics)
    {
        this.producer = producer;
        this.topics = new HashSet<string>(topics, StringComparer.Ordinal);
    }

    /// <summary>Wire JSON for typed messages: web defaults, enums as strings, defaults omitted.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>Starts a sender for <paramref name="topics"/>.</summary>
    public static KafkaSender Start(string bootstrapServers, IEnumerable<string> topics)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            LingerMs = 0,
        };
        return new KafkaSender(new ProducerBuilder<string, byte[]>(config).Build(), topics);
    }

    /// <summary>Produces <paramref name="message"/> with its CLR full name as the message type.</summary>
    public Task PublishAsync<T>(string topic, T message, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), JsonOptions);
        return PublishAsync(topic, body, TypeName(message.GetType()), partitionKey, correlationId, ct, headers);
    }

    /// <summary>Produces raw JSON text with an explicit message type.</summary>
    public Task PublishJsonAsync(string topic, string json, string type, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null) =>
        PublishAsync(topic, Encoding.UTF8.GetBytes(json), type, partitionKey, correlationId, ct, headers);

    /// <summary>Produces a raw body with an explicit message type.</summary>
    /// <remarks><paramref name="headers"/> become extra record headers alongside the type and correlation id.</remarks>
    public async Task PublishAsync(string topic, ReadOnlyMemory<byte> body, string type, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (!topics.Contains(topic))
            throw new InvalidOperationException($"Topic '{topic}' is not configured for publishing. Configured: {string.Join(", ", topics)}.");

        var recordHeaders = new Headers { { TypeHeader, Encoding.UTF8.GetBytes(type) } };
        if (correlationId is not null)
            recordHeaders.Add(CorrelationIdHeader, Encoding.UTF8.GetBytes(correlationId));
        foreach (var (name, value) in headers ?? [])
            recordHeaders.Add(name, Encoding.UTF8.GetBytes(value));

        await producer.ProduceAsync(topic, new Message<string, byte[]>
        {
            Key = partitionKey ?? Guid.NewGuid().ToString(),
            Value = body.ToArray(),
            Headers = recordHeaders,
        }, ct);
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
    public ValueTask DisposeAsync()
    {
        producer.Flush(TimeSpan.FromSeconds(5));
        producer.Dispose();
        return ValueTask.CompletedTask;
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
