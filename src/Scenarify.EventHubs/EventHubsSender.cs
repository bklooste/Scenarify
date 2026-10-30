using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

namespace Scenarify;

/// <summary>
/// Publishes real events onto Event Hubs from a test, standing in for an upstream service.
/// </summary>
/// <remarks>
/// The message type travels in the <c>Type</c> application property and the correlation id in the native
/// <see cref="EventData.CorrelationId"/>. With no partition key the service assigns a partition (round robin).
/// </remarks>
public sealed class EventHubsSender : IAsyncDisposable
{
    /// <summary>The application property that carries the message type.</summary>
    public const string TypeProperty = "Type";

    private readonly Dictionary<string, EventHubProducerClient> producers;

    private EventHubsSender(Dictionary<string, EventHubProducerClient> producers) => this.producers = producers;

    /// <summary>Wire JSON for typed messages: web defaults, enums as strings, defaults omitted.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>Starts a sender for <paramref name="hubs"/>; connects eagerly so a bad hub name fails here, not mid-test.</summary>
    public static async Task<EventHubsSender> StartAsync(string connectionString, IEnumerable<string> hubs, CancellationToken ct = default)
    {
        var producers = new Dictionary<string, EventHubProducerClient>(StringComparer.Ordinal);
        try
        {
            foreach (var hub in hubs)
            {
                var producer = new EventHubProducerClient(connectionString, hub);
                producers[hub] = producer;
                await producer.GetEventHubPropertiesAsync(ct);
            }
        }
        catch
        {
            foreach (var producer in producers.Values)
                await producer.DisposeAsync();
            throw;
        }
        return new EventHubsSender(producers);
    }

    /// <summary>Publishes <paramref name="message"/> with its CLR full name as the message type.</summary>
    public Task PublishAsync<T>(string hub, T message, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), JsonOptions);
        return PublishAsync(hub, body, TypeName(message.GetType()), partitionKey, correlationId, ct, headers);
    }

    /// <summary>Publishes raw JSON text with an explicit message type.</summary>
    public Task PublishJsonAsync(string hub, string json, string type, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null) =>
        PublishAsync(hub, Encoding.UTF8.GetBytes(json), type, partitionKey, correlationId, ct, headers);

    /// <summary>Publishes a raw body with an explicit message type.</summary>
    /// <remarks><paramref name="headers"/> become application properties alongside <c>Type</c>.</remarks>
    public async Task PublishAsync(string hub, ReadOnlyMemory<byte> body, string type, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (!producers.TryGetValue(hub, out var producer))
            throw new InvalidOperationException($"Event Hub '{hub}' is not configured for publishing. Configured: {string.Join(", ", producers.Keys)}.");

        var data = new EventData(body) { CorrelationId = correlationId, ContentType = "application/json" };
        data.Properties[TypeProperty] = type;
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
                data.Properties[name] = value;
        }
        await producer.SendAsync([data], new SendEventOptions { PartitionKey = partitionKey }, ct);
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
        foreach (var producer in producers.Values)
            await producer.DisposeAsync();
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
