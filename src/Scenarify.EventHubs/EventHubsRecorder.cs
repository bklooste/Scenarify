using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Primitives;

using Xunit.Sdk;

namespace Scenarify;

/// <summary>An event an <see cref="EventHubsRecorder"/> saw.</summary>
/// <remarks>Named apart from Scenarify.Redis's <c>RecordedMessage</c> so both add-ons can share the <c>Scenarify</c> namespace.</remarks>
public sealed record EventHubsMessage(string Topic, string Type, string PartitionKey, string? CorrelationId, byte[] Body, DateTimeOffset RecordedAt)
{
    /// <summary>Application properties other than <c>Type</c>, as invariant strings.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>The partition the event was read from.</summary>
    public string PartitionId { get; init; } = "";

    private JsonNode? json;
    private bool parsed;

    /// <summary>The body as text.</summary>
    public string Text => Encoding.UTF8.GetString(Body);

    /// <summary>The body parsed as JSON, or null when it is not JSON.</summary>
    public JsonNode? Json
    {
        get
        {
            if (!parsed)
            {
                try
                {
                    json = JsonNode.Parse(Body);
                }
                catch (JsonException)
                {
                    json = null;
                }
                parsed = true;
            }
            return json;
        }
    }

    /// <summary>The body deserialized as <typeparamref name="T"/> (web defaults unless <paramref name="options"/> given).</summary>
    public T? As<T>(JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(Body, options ?? EventHubsSender.JsonOptions);
}

/// <summary>
/// Records every event published to a set of Event Hubs, from the moment it starts, so tests can wait for
/// the one they care about. Messages that don't match a wait stay in the buffer for other tests. Tests must
/// filter by a unique id, which the JSON matcher does.
/// </summary>
/// <remarks>
/// One <see cref="PartitionReceiver"/> per partition, each pinned during start to just after the partition's
/// last enqueued sequence number (not <see cref="EventPosition.Latest"/>, which only resolves when the link
/// opens and can miss events sent in between). No checkpoints are written, so nothing replays or lingers.
/// The buffer drops events older than <see cref="MaxAge"/> and keeps at most <see cref="MaxPerTopic"/> per hub.
/// </remarks>
public sealed class EventHubsRecorder : IAsyncDisposable
{
    /// <summary>Messages older than this are evicted.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    /// <summary>At most this many messages are kept per hub.</summary>
    public const int MaxPerTopic = 20_000;

    private readonly object gate = new();
    private readonly Dictionary<string, List<EventHubsMessage>> byTopic;
    private readonly List<PartitionReceiver> receivers = [];
    private readonly List<Task> pumps = [];
    private readonly CancellationTokenSource stopping = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private EventHubsRecorder(IEnumerable<string> hubs)
    {
        byTopic = hubs.ToDictionary(t => t, _ => new List<EventHubsMessage>(), StringComparer.Ordinal);
    }

    /// <summary>Starts recording <paramref name="hubs"/> on <paramref name="consumerGroup"/> (default <c>$Default</c>).</summary>
    public static async Task<EventHubsRecorder> StartAsync(string connectionString, IEnumerable<string> hubs, string? consumerGroup = null,
        CancellationToken ct = default)
    {
        var group = consumerGroup ?? EventHubConsumerClient.DefaultConsumerGroupName;
        var recorder = new EventHubsRecorder(hubs);
        try
        {
            foreach (var hub in recorder.byTopic.Keys)
            {
                await using var consumer = new EventHubConsumerClient(group, connectionString, hub);
                foreach (var partitionId in await consumer.GetPartitionIdsAsync(ct))
                {
                    var props = await consumer.GetPartitionPropertiesAsync(partitionId, ct);
                    var start = props.IsEmpty
                        ? EventPosition.Earliest
                        : EventPosition.FromSequenceNumber(props.LastEnqueuedSequenceNumber, isInclusive: false);
                    var receiver = new PartitionReceiver(group, partitionId, start, connectionString, hub);
                    recorder.receivers.Add(receiver);
                    recorder.pumps.Add(Task.Run(() => recorder.PumpAsync(hub, receiver, recorder.stopping.Token), CancellationToken.None));
                }
            }
        }
        catch
        {
            await recorder.DisposeAsync();
            throw;
        }
        return recorder;
    }

    /// <summary>A snapshot of what has been recorded on <paramref name="topic"/>, optionally of one type.</summary>
    public IReadOnlyList<EventHubsMessage> Messages(string topic, string? type = null)
    {
        lock (gate)
        {
            var list = Topic(topic);
            return type is null ? [.. list] : list.Where(m => m.Type == type).ToList();
        }
    }

    /// <summary>
    /// Waits for an event on <paramref name="topic"/> (of <paramref name="type"/> when given) that satisfies
    /// <paramref name="predicate"/>. On timeout the message includes <paramref name="explain"/>'s account of the candidates.
    /// </summary>
    public async Task<EventHubsMessage> WaitForAsync(string topic, string? type, Func<EventHubsMessage, bool> predicate, TimeSpan timeout,
        string description, Func<IReadOnlyList<EventHubsMessage>, string>? explain = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        List<EventHubsMessage> candidates;
        while (true)
        {
            Task signal;
            lock (gate)
            {
                candidates = Topic(topic).Where(m => type is null || m.Type == type).ToList();
                signal = changed.Task;
            }

            var hit = candidates.FirstOrDefault(predicate);
            if (hit is not null)
                return hit;

            try
            {
                await signal.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!Xunit.TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        var sb = new StringBuilder();
        sb.Append($"No event on '{topic}'{(type is null ? "" : $" of type {type}")} matched {description} within {timeout.TotalSeconds:0.#}s.");
        sb.AppendLine().Append($"{candidates.Count} candidate(s) of that type recorded; hub types seen: {DescribeTypes(topic)}.");
        if (explain is not null && candidates.Count > 0)
            sb.AppendLine().Append(explain(candidates));
        throw new XunitException(sb.ToString());
    }

    /// <summary>Waits for a <typeparamref name="T"/> event satisfying <paramref name="predicate"/> and returns it deserialized.</summary>
    public async Task<T> WaitForAsync<T>(string topic, Func<T, bool> predicate, TimeSpan timeout, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var hit = await WaitForAsync(topic, EventHubsSender.TypeName(typeof(T)), m =>
        {
            var value = m.As<T>(options);
            return value is not null && predicate(value);
        }, timeout, $"a {typeof(T).Name} predicate");
        return hit.As<T>(options)!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        await Task.WhenAll(pumps);
        foreach (var receiver in receivers)
            await receiver.DisposeAsync();
        stopping.Dispose();
    }

    internal static string ExplainNearest(JsonNode? expected, IReadOnlyList<EventHubsMessage> candidates, JsonMatchOptions? options)
    {
        var nearest = candidates
            .Select(m => (Message: m, Diff: JsonMatch.Compare(expected, m.Json, options)))
            .OrderBy(x => x.Diff.Count)
            .Take(3);
        var sb = new StringBuilder("Nearest candidates:");
        foreach (var (message, diff) in nearest)
            sb.AppendLine().Append(JsonMatch.FormatFailure(diff, message.Json, $"key {message.PartitionKey}"));
        return sb.ToString();
    }

    private async Task PumpAsync(string hub, PartitionReceiver receiver, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var batch = await receiver.ReceiveBatchAsync(100, TimeSpan.FromSeconds(1), ct);
                var now = DateTimeOffset.UtcNow;
                var copies = batch.Select(e => Copy(hub, receiver.PartitionId, e, now)).ToArray();
                if (copies.Length > 0)
                    Add(hub, copies);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Transient link failures: the receiver reconnects at its last position on the next call.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static EventHubsMessage Copy(string hub, string partitionId, EventData e, DateTimeOffset now)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        string type = "";
        foreach (var (name, value) in e.Properties)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (name == EventHubsSender.TypeProperty)
                type = text;
            else
                headers[name] = text;
        }
        return new EventHubsMessage(hub, type, e.PartitionKey ?? "", e.CorrelationId, e.EventBody.ToArray(), now)
        {
            Headers = headers,
            PartitionId = partitionId,
        };
    }

    private void Add(string topic, EventHubsMessage[] messages)
    {
        TaskCompletionSource previous;
        lock (gate)
        {
            var list = byTopic[topic];
            list.AddRange(messages);
            var cutoff = DateTimeOffset.UtcNow - MaxAge;
            var expired = list.FindIndex(m => m.RecordedAt >= cutoff);
            var drop = Math.Max(expired < 0 ? list.Count : expired, list.Count - MaxPerTopic);
            if (drop > 0)
                list.RemoveRange(0, drop);

            previous = changed;
            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        previous.TrySetResult();
    }

    private List<EventHubsMessage> Topic(string topic) =>
        byTopic.TryGetValue(topic, out var list)
            ? list
            : throw new InvalidOperationException($"Event Hub '{topic}' is not recorded. Recorded: {string.Join(", ", byTopic.Keys)}.");

    private string DescribeTypes(string topic)
    {
        lock (gate)
        {
            var groups = Topic(topic).GroupBy(m => m.Type).Select(g => $"{g.Key} x{g.Count()}").ToList();
            return groups.Count == 0 ? "none" : string.Join(", ", groups);
        }
    }
}
