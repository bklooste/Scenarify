using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Amazon.Kinesis;
using Amazon.Kinesis.Model;

using Xunit.Sdk;

namespace Scenarify;

/// <summary>A record a <see cref="KinesisRecorder"/> saw.</summary>
/// <remarks>
/// Shaped like the other add-ons' <c>RecordedMessage</c>, but Kinesis has no message type or headers:
/// <see cref="Type"/> is always null and <see cref="Headers"/> always empty.
/// </remarks>
public sealed record KinesisRecordedMessage(string Topic, string PartitionKey, string SequenceNumber, byte[] Body,
    DateTimeOffset? ArrivedAt, DateTimeOffset RecordedAt)
{
    /// <summary>Always null: Kinesis records carry no type.</summary>
    public string? Type => null;

    /// <summary>Always empty: Kinesis records carry no headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

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
        JsonSerializer.Deserialize<T>(Body, options ?? KinesisSender.JsonOptions);
}

/// <summary>
/// Records every record put on a set of Kinesis streams from the moment it starts, so tests can wait for the one they
/// care about. Records that don't match a wait stay in the buffer for other tests; filter by a unique id in the body.
/// </summary>
/// <remarks>
/// <para>Each shard is read from a <c>LATEST</c> iterator taken during start, so earlier records are never replayed.
/// The buffer drops records older than <see cref="MaxAge"/> and keeps at most <see cref="MaxPerTopic"/> per stream.</para>
/// <para>One poll loop per shard, every <see cref="PollInterval"/>. That is tuned for a local or emulated stream with a
/// few shards, not AWS's real per-shard <c>GetRecords</c> limit (5 calls/s shared by all readers). Shards that appear
/// later (resharding) are not followed; a shard whose iterator comes back null is closed and its loop stops.</para>
/// </remarks>
public sealed class KinesisRecorder : IAsyncDisposable
{
    /// <summary>Records older than this are evicted.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    /// <summary>At most this many records are kept per stream.</summary>
    public const int MaxPerTopic = 20_000;

    /// <summary>Delay between <c>GetRecords</c> calls on one shard.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly object gate = new();
    private readonly Dictionary<string, List<KinesisRecordedMessage>> byStream;
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> loops = [];
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private KinesisRecorder(IEnumerable<string> streams)
    {
        byStream = streams.ToDictionary(s => s, _ => new List<KinesisRecordedMessage>(), StringComparer.Ordinal);
    }

    /// <summary>Starts recording <paramref name="streams"/>, which must exist and be <c>ACTIVE</c>.</summary>
    public static async Task<KinesisRecorder> StartAsync(IAmazonKinesis client, IEnumerable<string> streams, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var list = streams.ToList();
        var recorder = new KinesisRecorder(list);
        try
        {
            foreach (var stream in list)
            {
                var description = await client.DescribeStreamAsync(new DescribeStreamRequest { StreamName = stream }, ct);
                foreach (var shard in description.StreamDescription.Shards ?? [])
                {
                    // Iterators are taken here, before start returns, so "now" is pinned like StartFrom=Now.
                    var iterator = await client.GetShardIteratorAsync(new GetShardIteratorRequest
                    {
                        StreamName = stream,
                        ShardId = shard.ShardId,
                        ShardIteratorType = ShardIteratorType.LATEST,
                    }, ct);
                    recorder.loops.Add(Task.Run(() => recorder.PollAsync(client, stream, iterator.ShardIterator)));
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

    /// <summary>A snapshot of what has been recorded on <paramref name="stream"/>.</summary>
    public IReadOnlyList<KinesisRecordedMessage> Messages(string stream)
    {
        lock (gate)
            return [.. Stream(stream)];
    }

    /// <summary>
    /// Waits for a record on <paramref name="stream"/> that satisfies <paramref name="predicate"/>. On timeout the
    /// message includes <paramref name="explain"/>'s account of the candidates.
    /// </summary>
    public async Task<KinesisRecordedMessage> WaitForAsync(string stream, Func<KinesisRecordedMessage, bool> predicate, TimeSpan timeout,
        string description, Func<IReadOnlyList<KinesisRecordedMessage>, string>? explain = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        List<KinesisRecordedMessage> candidates;
        while (true)
        {
            Task signal;
            lock (gate)
            {
                candidates = [.. Stream(stream)];
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
        sb.Append($"No record on '{stream}' matched {description} within {timeout.TotalSeconds:0.#}s.");
        sb.AppendLine().Append($"{candidates.Count} record(s) recorded on the stream.");
        if (explain is not null && candidates.Count > 0)
            sb.AppendLine().Append(explain(candidates));
        throw new XunitException(sb.ToString());
    }

    /// <summary>Waits for a record whose JSON body subset-matches <paramref name="expected"/>.</summary>
    public Task<KinesisRecordedMessage> WaitForAsync(string stream, JsonSource expected, TimeSpan timeout,
        ScenarioVariables? variables = null, JsonMatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var resolved = expected.Resolve(variables);
        return WaitForAsync(stream, m => JsonMatch.IsMatch(resolved, m.Json, options), timeout,
            $"{expected.Describe()} {resolved?.ToJsonString()}",
            candidates => ExplainNearest(resolved, candidates, options));
    }

    /// <summary>
    /// Waits for a record whose body deserializes as <typeparamref name="T"/> and satisfies <paramref name="predicate"/>.
    /// <typeparamref name="T"/> is only a deserialization target; records are not filtered by type.
    /// </summary>
    public async Task<T> WaitForAsync<T>(string stream, Func<T, bool> predicate, TimeSpan timeout, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var hit = await WaitForAsync(stream, m =>
        {
            T? value;
            try
            {
                value = m.As<T>(options);
            }
            catch (JsonException)
            {
                return false;
            }
            return value is not null && predicate(value);
        }, timeout, $"a {typeof(T).Name} predicate");
        return hit.As<T>(options)!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!stop.IsCancellationRequested)
            await stop.CancelAsync();
        try
        {
            await Task.WhenAll(loops);
        }
        catch (OperationCanceledException)
        {
        }
        stop.Dispose();
    }

    internal static string ExplainNearest(JsonNode? expected, IReadOnlyList<KinesisRecordedMessage> candidates, JsonMatchOptions? options)
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

    private async Task PollAsync(IAmazonKinesis client, string stream, string? iterator)
    {
        var ct = stop.Token;
        while (iterator is not null && !ct.IsCancellationRequested)
        {
            GetRecordsResponse response;
            try
            {
                response = await client.GetRecordsAsync(new GetRecordsRequest { ShardIterator = iterator, Limit = 1000 }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ProvisionedThroughputExceededException)
            {
                await DelayAsync(ct);
                continue;
            }

            var records = response.Records ?? [];
            if (records.Count > 0)
            {
                var now = DateTimeOffset.UtcNow;
                Add(stream, [.. records.Select(r => new KinesisRecordedMessage(
                    stream, r.PartitionKey, r.SequenceNumber, r.Data.ToArray(),
                    r.ApproximateArrivalTimestamp is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null,
                    now))]);
            }

            // Null means the shard is closed (merged or split): nothing more will arrive on it.
            iterator = response.NextShardIterator;
            if (!await DelayAsync(ct))
                return;
        }
    }

    private static async Task<bool> DelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(PollInterval, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Add(string stream, KinesisRecordedMessage[] messages)
    {
        TaskCompletionSource previous;
        lock (gate)
        {
            var list = byStream[stream];
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

    private List<KinesisRecordedMessage> Stream(string stream) =>
        byStream.TryGetValue(stream, out var list)
            ? list
            : throw new InvalidOperationException($"Stream '{stream}' is not recorded. Recorded: {string.Join(", ", byStream.Keys)}.");
}
