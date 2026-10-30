using System.Text.Json;
using System.Text.Json.Serialization;

using Azure.Messaging.ServiceBus;

using SdkSender = Azure.Messaging.ServiceBus.ServiceBusSender;

namespace Scenarify;

/// <summary>
/// Sends real messages to Service Bus queues or topics from a test, standing in for an upstream service.
/// </summary>
/// <remarks>
/// The message type travels in <see cref="ServiceBusMessage.Subject"/>; correlation id and partition key use the native
/// properties (the partition key only affects routing on partitioned entities); headers become application properties.
/// </remarks>
public sealed class ServiceBusSender : IAsyncDisposable
{
    private readonly ServiceBusClient client;
    private readonly Dictionary<string, SdkSender> senders;

    private ServiceBusSender(ServiceBusClient client, IEnumerable<string> entities)
    {
        this.client = client;
        senders = entities.Distinct(StringComparer.Ordinal).ToDictionary(e => e, client.CreateSender, StringComparer.Ordinal);
    }

    /// <summary>Wire JSON for typed messages: web defaults, enums as strings, defaults omitted.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>Creates a sender for <paramref name="entities"/> (queue or topic names). Connects lazily on first send.</summary>
    public static ServiceBusSender Start(string connectionString, IEnumerable<string> entities) =>
        new(new ServiceBusClient(connectionString), entities);

    /// <summary>Sends <paramref name="message"/> with its CLR full name as the subject.</summary>
    public Task PublishAsync<T>(string entity, T message, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), JsonOptions);
        return PublishAsync(entity, body, TypeName(message.GetType()), partitionKey, correlationId, ct, headers);
    }

    /// <summary>Sends raw JSON text with an explicit message type.</summary>
    public Task PublishJsonAsync(string entity, string json, string type, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null) =>
        PublishAsync(entity, System.Text.Encoding.UTF8.GetBytes(json), type, partitionKey, correlationId, ct, headers, "application/json");

    /// <summary>Sends a raw body with an explicit message type.</summary>
    public async Task PublishAsync(string entity, ReadOnlyMemory<byte> body, string type, string? partitionKey = null, string? correlationId = null, CancellationToken ct = default,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (!senders.TryGetValue(entity, out var sender))
            throw new InvalidOperationException($"Entity '{entity}' is not configured for sending. Configured: {string.Join(", ", senders.Keys)}.");

        var message = new ServiceBusMessage(body) { Subject = type, ContentType = contentType };
        if (correlationId is not null)
            message.CorrelationId = correlationId;
        if (partitionKey is not null)
            message.PartitionKey = partitionKey;
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
                message.ApplicationProperties[name] = value;
        }
        await sender.SendMessageAsync(message, ct);
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
        foreach (var sender in senders.Values)
            await sender.DisposeAsync();
        await client.DisposeAsync();
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
