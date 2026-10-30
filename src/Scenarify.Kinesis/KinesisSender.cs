using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Amazon.Kinesis;
using Amazon.Kinesis.Model;

namespace Scenarify;

/// <summary>
/// Puts real records onto Kinesis streams from a test, standing in for an upstream producer.
/// </summary>
/// <remarks>
/// A Kinesis record is only <c>(Data, PartitionKey)</c>: there is no message type, correlation id or header,
/// so none are sent. The body is exactly what a plain <c>PutRecord</c> producer would write.
/// </remarks>
public sealed class KinesisSender
{
    private readonly IAmazonKinesis client;
    private readonly HashSet<string> streams;

    /// <summary>A sender for <paramref name="streams"/> (which must already exist) on <paramref name="client"/>.</summary>
    public KinesisSender(IAmazonKinesis client, IEnumerable<string> streams)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(streams);
        this.client = client;
        this.streams = new HashSet<string>(streams, StringComparer.Ordinal);
    }

    /// <summary>
    /// Wire JSON for typed messages: web defaults, enums as strings, defaults omitted (same convention as the
    /// other Scenarify add-ons).
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>Puts <paramref name="message"/> serialized as JSON; a fresh GUID partition key when none is given.</summary>
    public Task<PutRecordResponse> PublishAsync<T>(string stream, T message, string? partitionKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), JsonOptions);
        return PublishAsync(stream, (ReadOnlyMemory<byte>)body, partitionKey, ct);
    }

    /// <summary>Puts raw JSON text.</summary>
    public Task<PutRecordResponse> PublishJsonAsync(string stream, string json, string? partitionKey = null, CancellationToken ct = default) =>
        PublishAsync(stream, (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(json), partitionKey, ct);

    /// <summary>Puts a raw body.</summary>
    public async Task<PutRecordResponse> PublishAsync(string stream, ReadOnlyMemory<byte> body, string? partitionKey = null, CancellationToken ct = default)
    {
        if (!streams.Contains(stream))
            throw new InvalidOperationException($"Stream '{stream}' is not configured for publishing. Configured: {string.Join(", ", streams)}.");

        using var data = new MemoryStream(body.ToArray(), writable: false);
        return await client.PutRecordAsync(new PutRecordRequest
        {
            StreamName = stream,
            Data = data,
            PartitionKey = partitionKey ?? Guid.NewGuid().ToString(),
        }, ct);
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
