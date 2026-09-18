using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using RedisEvents.Extensions;

using Xunit.Sdk;

namespace Scenarify;

/// <summary>A message a <see cref="StreamRecorder"/> saw.</summary>
public sealed record RecordedMessage(string Topic, string Type, string PartitionKey, string? CorrelationId, byte[] Body, DateTimeOffset RecordedAt)
{
    /// <summary>The message headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

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
        JsonSerializer.Deserialize<T>(Body, options ?? StreamSender.JsonOptions);
}

/// <summary>
/// Records every message published to a set of topics, from the moment it starts, so tests can wait for
/// the one they care about. One recorder (one consumer host) per fixture; messages that don't match a wait
/// stay in the buffer for other tests. Tests must filter by a unique id, which the JSON matcher does.
/// </summary>
/// <remarks>
/// Consumers start from <c>Now</c> (pinned to the server's tail during start) with <c>Persist = None</c>, so a
/// recorder never replays earlier runs and never leaves positions behind. The buffer drops messages older
/// than <see cref="MaxAge"/> and keeps at most <see cref="MaxPerTopic"/> per topic.
/// </remarks>
public sealed class StreamRecorder : IAsyncDisposable
{
    /// <summary>Messages older than this are evicted.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    /// <summary>At most this many messages are kept per topic.</summary>
    public const int MaxPerTopic = 20_000;

    private readonly object gate = new();
    private readonly Dictionary<string, List<RecordedMessage>> byTopic;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IHost? host;

    private StreamRecorder(IEnumerable<string> topics)
    {
        byTopic = topics.ToDictionary(t => t, _ => new List<RecordedMessage>(), StringComparer.Ordinal);
    }

    /// <summary>Starts recording <paramref name="topics"/>.</summary>
    public static async Task<StreamRecorder> StartAsync(string connectionString, IEnumerable<string> topics,
        IReadOnlyDictionary<string, int>? partitions = null, CancellationToken ct = default)
    {
        var list = topics.ToList();
        var recorder = new StreamRecorder(list);
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var settings = StreamConfig.Build(connectionString, list, partitions);
        var consumerName = $"test-recorder-{Guid.NewGuid():N}";
        for (var i = 0; i < list.Count; i++)
        {
            settings[$"Streams:Consumers:{i}:Topic"] = list[i];
            settings[$"Streams:Consumers:{i}:Consumer"] = consumerName;
            settings[$"Streams:Consumers:{i}:StartFrom"] = "Now";
            settings[$"Streams:Consumers:{i}:Persist"] = "None";
            settings[$"Streams:Consumers:{i}:BatchSize"] = "100";
        }
        builder.Configuration.AddInMemoryCollection(settings);

        foreach (var topic in list)
        {
            builder.AddStream(topic, (batch, _) =>
            {
                var now = DateTimeOffset.UtcNow;
                var copies = new RecordedMessage[batch.Length];
                for (var i = 0; i < batch.Length; i++)
                {
                    // The batch is borrowed memory: copy the body out before returning.
                    var msg = batch.Span[i];
                    var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var header in msg.Headers)
                        headers[header.Key] = header.Value;
                    copies[i] = new RecordedMessage(topic, msg.Type, msg.PartitionKey, msg.CorrelationId, msg.Body.ToArray(), now) { Headers = headers };
                }
                recorder.Add(topic, copies);
                return ValueTask.CompletedTask;
            });
        }

        recorder.host = builder.Build();
        await recorder.host.StartAsync(ct);
        return recorder;
    }

    /// <summary>A snapshot of what has been recorded on <paramref name="topic"/>, optionally of one type.</summary>
    public IReadOnlyList<RecordedMessage> Messages(string topic, string? type = null)
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
    public async Task<RecordedMessage> WaitForAsync(string topic, string? type, Func<RecordedMessage, bool> predicate, TimeSpan timeout,
        string description, Func<IReadOnlyList<RecordedMessage>, string>? explain = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        List<RecordedMessage> candidates;
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
        sb.AppendLine().Append($"{candidates.Count} candidate(s) of that type recorded; topic types seen: {DescribeTypes(topic)}.");
        if (explain is not null && candidates.Count > 0)
            sb.AppendLine().Append(explain(candidates));
        throw new XunitException(sb.ToString());
    }

    /// <summary>Waits for a message of <paramref name="type"/> whose JSON body subset-matches <paramref name="expected"/>.</summary>
    public Task<RecordedMessage> WaitForAsync(string topic, string? type, JsonSource expected, TimeSpan timeout,
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
        var hit = await WaitForAsync(topic, StreamSender.TypeName(typeof(T)), m =>
        {
            var value = m.As<T>(options);
            return value is not null && predicate(value);
        }, timeout, $"a {typeof(T).Name} predicate");
        return hit.As<T>(options)!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    internal static string ExplainNearest(JsonNode? expected, IReadOnlyList<RecordedMessage> candidates, JsonMatchOptions? options)
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

    private void Add(string topic, RecordedMessage[] messages)
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

    private List<RecordedMessage> Topic(string topic) =>
        byTopic.TryGetValue(topic, out var list)
            ? list
            : throw new InvalidOperationException($"Topic '{topic}' is not recorded. Recorded: {string.Join(", ", byTopic.Keys)}.");

    private string DescribeTypes(string topic)
    {
        lock (gate)
        {
            var groups = Topic(topic).GroupBy(m => m.Type).Select(g => $"{g.Key} x{g.Count()}").ToList();
            return groups.Count == 0 ? "none" : string.Join(", ", groups);
        }
    }
}
