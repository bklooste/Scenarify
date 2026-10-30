using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Azure.Messaging.ServiceBus;

using Xunit.Sdk;

namespace Scenarify;

/// <summary>A message a <see cref="ServiceBusRecorder"/> received.</summary>
/// <remarks><c>Topic</c> is the recorded entity name (queue or topic); <c>Type</c> is the message subject.</remarks>
public sealed record ServiceBusRecordedMessage(string Topic, string Type, string PartitionKey, string? CorrelationId, byte[] Body, DateTimeOffset RecordedAt)
{
    /// <summary>The application properties, as invariant strings.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>The broker's message id.</summary>
    public string? MessageId { get; init; }

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
        JsonSerializer.Deserialize<T>(Body, options ?? ServiceBusSender.JsonOptions);
}

/// <summary>
/// Records every message arriving on a set of queues or topic subscriptions, so tests can wait for the one they care
/// about. One recorder per fixture; messages that don't match a wait stay in the buffer for other tests. Tests must
/// filter by a unique id, which the JSON matcher does.
/// </summary>
/// <remarks>
/// <para>Receivers use <see cref="ServiceBusReceiveMode.ReceiveAndDelete"/>: the recorder consumes the entity, so it must
/// not share a queue or subscription with the service under test — give the recorder its own subscription.</para>
/// <para>Entities are durable, so messages enqueued before the recorder started (earlier runs) are received too; those
/// enqueued more than a second before start are dropped to keep "from now" semantics. The buffer drops messages older
/// than <see cref="MaxAge"/> and keeps at most <see cref="MaxPerTopic"/> per entity.</para>
/// </remarks>
public sealed class ServiceBusRecorder : IAsyncDisposable
{
    /// <summary>Messages older than this are evicted.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    /// <summary>At most this many messages are kept per entity.</summary>
    public const int MaxPerTopic = 20_000;

    private static readonly TimeSpan StartSkew = TimeSpan.FromSeconds(1);

    private readonly object gate = new();
    private readonly Dictionary<string, List<ServiceBusRecordedMessage>> byTopic;
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Task> loops = [];
    private readonly List<ServiceBusReceiver> receivers = [];
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ServiceBusClient? client;

    private ServiceBusRecorder(IEnumerable<string> entities)
    {
        byTopic = entities.Distinct(StringComparer.Ordinal).ToDictionary(t => t, _ => new List<ServiceBusRecordedMessage>(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Starts recording <paramref name="entities"/>. An entity with an entry in <paramref name="subscriptions"/> is a topic
    /// received through that subscription; any other is a queue.
    /// </summary>
    public static async Task<ServiceBusRecorder> StartAsync(string connectionString, IEnumerable<string> entities,
        IReadOnlyDictionary<string, string>? subscriptions = null, CancellationToken ct = default)
    {
        var recorder = new ServiceBusRecorder(entities);
        recorder.client = new ServiceBusClient(connectionString);
        var options = new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete, PrefetchCount = 0 };
        foreach (var entity in recorder.byTopic.Keys)
        {
            var receiver = subscriptions is not null && subscriptions.TryGetValue(entity, out var subscription)
                ? recorder.client.CreateReceiver(entity, subscription, options)
                : recorder.client.CreateReceiver(entity, options);
            // Opens the link (and fails fast on a missing entity) before the loop starts.
            await receiver.PeekMessageAsync(cancellationToken: ct);
            recorder.receivers.Add(receiver);
            recorder.loops.Add(Task.Run(() => recorder.ReceiveLoop(entity, receiver), CancellationToken.None));
        }
        return recorder;
    }

    /// <summary>A snapshot of what has been recorded on <paramref name="topic"/>, optionally of one type (subject).</summary>
    public IReadOnlyList<ServiceBusRecordedMessage> Messages(string topic, string? type = null)
    {
        lock (gate)
        {
            var list = Topic(topic);
            return type is null ? [.. list] : list.Where(m => m.Type == type).ToList();
        }
    }

    /// <summary>
    /// Waits for a message on <paramref name="topic"/> (of <paramref name="type"/> when given) that satisfies
    /// <paramref name="predicate"/>. On timeout the message includes <paramref name="explain"/>'s account of the candidates.
    /// </summary>
    public async Task<ServiceBusRecordedMessage> WaitForAsync(string topic, string? type, Func<ServiceBusRecordedMessage, bool> predicate, TimeSpan timeout,
        string description, Func<IReadOnlyList<ServiceBusRecordedMessage>, string>? explain = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        List<ServiceBusRecordedMessage> candidates;
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
        sb.Append($"No message on '{topic}'{(type is null ? "" : $" of type {type}")} matched {description} within {timeout.TotalSeconds:0.#}s.");
        sb.AppendLine().Append($"{candidates.Count} candidate(s) of that type recorded; entity types seen: {DescribeTypes(topic)}.");
        if (explain is not null && candidates.Count > 0)
            sb.AppendLine().Append(explain(candidates));
        throw new XunitException(sb.ToString());
    }

    /// <summary>Waits for a message of <paramref name="type"/> whose JSON body subset-matches <paramref name="expected"/>.</summary>
    public Task<ServiceBusRecordedMessage> WaitForAsync(string topic, string? type, JsonSource expected, TimeSpan timeout,
        ScenarioVariables? variables = null, JsonMatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var resolved = expected.Resolve(variables);
        return WaitForAsync(topic, type, m => JsonMatch.IsMatch(resolved, m.Json, options), timeout,
            $"{expected.Describe()} {resolved?.ToJsonString()}",
            candidates => ExplainNearest(resolved, candidates, options));
    }

    /// <summary>Waits for a message of <typeparamref name="T"/> that satisfies <paramref name="predicate"/>.</summary>
    public async Task<T> WaitForAsync<T>(string topic, Func<T, bool> predicate, TimeSpan timeout, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var hit = await WaitForAsync(topic, ServiceBusSender.TypeName(typeof(T)), m =>
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
        try
        {
            await Task.WhenAll(loops);
        }
        catch (OperationCanceledException)
        {
        }
        foreach (var receiver in receivers)
            await receiver.DisposeAsync();
        if (client is not null)
            await client.DisposeAsync();
        stopping.Dispose();
    }

    internal static string ExplainNearest(JsonNode? expected, IReadOnlyList<ServiceBusRecordedMessage> candidates, JsonMatchOptions? options)
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

    private async Task ReceiveLoop(string entity, ServiceBusReceiver receiver)
    {
        var ct = stopping.Token;
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<ServiceBusReceivedMessage> batch;
            try
            {
                batch = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ServiceBusException ex) when (ex.IsTransient)
            {
                await Task.Delay(200, CancellationToken.None);
                continue;
            }

            if (batch.Count == 0)
                continue;
            var now = DateTimeOffset.UtcNow;
            var copies = batch
                .Where(m => m.EnqueuedTime >= startedAt - StartSkew)
                .Select(m => new ServiceBusRecordedMessage(entity, m.Subject ?? "", m.PartitionKey ?? "", m.CorrelationId, m.Body.ToArray(), now)
                {
                    MessageId = m.MessageId,
                    Headers = m.ApplicationProperties.ToDictionary(
                        p => p.Key, p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal),
                })
                .ToArray();
            if (copies.Length > 0)
                Add(entity, copies);
        }
    }

    private void Add(string topic, ServiceBusRecordedMessage[] messages)
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

    private List<ServiceBusRecordedMessage> Topic(string topic) =>
        byTopic.TryGetValue(topic, out var list)
            ? list
            : throw new InvalidOperationException($"Entity '{topic}' is not recorded. Recorded: {string.Join(", ", byTopic.Keys)}.");

    private string DescribeTypes(string topic)
    {
        lock (gate)
        {
            var groups = Topic(topic).GroupBy(m => m.Type).Select(g => $"{g.Key} x{g.Count()}").ToList();
            return groups.Count == 0 ? "none" : string.Join(", ", groups);
        }
    }
}
