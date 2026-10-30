using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using Xunit.Sdk;

namespace Scenarify;

/// <summary>A record a <see cref="KafkaRecorder"/> saw.</summary>
/// <remarks>Named apart from the Redis add-on's <c>RecordedMessage</c> so both packages can be referenced together.</remarks>
public sealed record KafkaMessage(string Topic, string Type, string PartitionKey, string? CorrelationId, byte[] Body, DateTimeOffset RecordedAt)
{
    /// <summary>The record headers other than <c>type</c> and <c>correlationId</c> (last value wins for repeats).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>The partition the record was read from.</summary>
    public int Partition { get; init; }

    /// <summary>The record's offset in its partition.</summary>
    public long Offset { get; init; }

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
        JsonSerializer.Deserialize<T>(Body, options ?? KafkaSender.JsonOptions);
}

/// <summary>
/// Records every Kafka record produced to a set of topics, from the moment it starts, so tests can wait for
/// the one they care about. One recorder (one consumer) per fixture; records that don't match a wait stay in
/// the buffer for other tests. Tests must filter by a unique id, which the JSON matcher does.
/// </summary>
/// <remarks>
/// <para>Start creates missing topics (broker-default partitions), joins a fresh consumer group, and seeks every
/// assigned partition to its high watermark before returning, so a record produced after start is always seen and
/// one produced before never is. Offsets are never committed.</para>
/// <para>The buffer drops records older than <see cref="MaxAge"/> and keeps at most <see cref="MaxPerTopic"/> per topic.</para>
/// </remarks>
public sealed class KafkaRecorder : IAsyncDisposable
{
    /// <summary>Records older than this are evicted.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    /// <summary>At most this many records are kept per topic.</summary>
    public const int MaxPerTopic = 20_000;

    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(100);

    private readonly object gate = new();
    private readonly Dictionary<string, List<KafkaMessage>> byTopic;
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource assigned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IConsumer<string, byte[]>? consumer;
    private Task? loop;

    private KafkaRecorder(IEnumerable<string> topics)
    {
        byTopic = topics.ToDictionary(t => t, _ => new List<KafkaMessage>(), StringComparer.Ordinal);
    }

    /// <summary>Starts recording <paramref name="topics"/>; returns once every partition is positioned at its tail.</summary>
    public static async Task<KafkaRecorder> StartAsync(string bootstrapServers, IEnumerable<string> topics, CancellationToken ct = default)
    {
        var list = topics.Distinct(StringComparer.Ordinal).ToList();
        var partitionCount = await EnsureTopicsAsync(bootstrapServers, list, ct);
        var recorder = new KafkaRecorder(list);

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = $"test-recorder-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            EnablePartitionEof = false,
        };
        recorder.consumer = new ConsumerBuilder<string, byte[]>(config)
            // Pin to the high watermark rather than trusting "latest", which resolves lazily on first fetch and so
            // can land after a record produced between assignment and that fetch.
            .SetPartitionsAssignedHandler((c, partitions) =>
            {
                var positioned = partitions
                    .Select(tp => new TopicPartitionOffset(tp, c.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(10)).High))
                    .ToList();
                if (positioned.Count >= partitionCount)
                    recorder.assigned.TrySetResult();
                return positioned;
            })
            .Build();
        recorder.consumer.Subscribe(list);
        recorder.loop = Task.Factory.StartNew(recorder.Run, TaskCreationOptions.LongRunning);

        try
        {
            await recorder.assigned.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        }
        catch
        {
            await recorder.DisposeAsync();
            throw;
        }
        return recorder;
    }

    /// <summary>A snapshot of what has been recorded on <paramref name="topic"/>, optionally of one type.</summary>
    public IReadOnlyList<KafkaMessage> Messages(string topic, string? type = null)
    {
        lock (gate)
        {
            var list = Topic(topic);
            return type is null ? [.. list] : list.Where(m => m.Type == type).ToList();
        }
    }

    /// <summary>
    /// Waits for a record on <paramref name="topic"/> (of <paramref name="type"/> when given) that satisfies
    /// <paramref name="predicate"/>. On timeout the message includes <paramref name="explain"/>'s account of the candidates.
    /// </summary>
    public async Task<KafkaMessage> WaitForAsync(string topic, string? type, Func<KafkaMessage, bool> predicate, TimeSpan timeout,
        string description, Func<IReadOnlyList<KafkaMessage>, string>? explain = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        List<KafkaMessage> candidates;
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
        sb.Append($"No record on '{topic}'{(type is null ? "" : $" of type {type}")} matched {description} within {timeout.TotalSeconds:0.#}s.");
        sb.AppendLine().Append($"{candidates.Count} candidate(s) of that type recorded; topic types seen: {DescribeTypes(topic)}.");
        if (explain is not null && candidates.Count > 0)
            sb.AppendLine().Append(explain(candidates));
        throw new XunitException(sb.ToString());
    }

    /// <summary>Waits for a record of <paramref name="type"/> whose JSON body subset-matches <paramref name="expected"/>.</summary>
    public Task<KafkaMessage> WaitForAsync(string topic, string? type, JsonSource expected, TimeSpan timeout,
        ScenarioVariables? variables = null, JsonMatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var resolved = expected.Resolve(variables);
        return WaitForAsync(topic, type, m => JsonMatch.IsMatch(resolved, m.Json, options), timeout,
            $"{expected.Describe()} {resolved?.ToJsonString()}",
            candidates => ExplainNearest(resolved, candidates, options));
    }

    /// <summary>Waits for a record of <typeparamref name="T"/> that satisfies <paramref name="predicate"/>.</summary>
    public async Task<T> WaitForAsync<T>(string topic, Func<T, bool> predicate, TimeSpan timeout, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var hit = await WaitForAsync(topic, KafkaSender.TypeName(typeof(T)), m =>
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
        if (loop is not null)
            await loop;
        if (consumer is not null)
        {
            consumer.Close();
            consumer.Dispose();
        }
        stopping.Dispose();
    }

    internal static string ExplainNearest(JsonNode? expected, IReadOnlyList<KafkaMessage> candidates, JsonMatchOptions? options)
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

    private static async Task<int> EnsureTopicsAsync(string bootstrapServers, List<string> topics, CancellationToken ct)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(topics.Select(t => new TopicSpecification { Name = t, NumPartitions = -1, ReplicationFactor = -1 }));
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
        }

        // A just-created topic takes a moment to get partition leaders; assignment and watermarks need them.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var metadata = topics.Select(t => admin.GetMetadata(t, TimeSpan.FromSeconds(5)).Topics.Single()).ToList();
            if (metadata.All(t => t.Error.Code == ErrorCode.NoError && t.Partitions.Count > 0 && t.Partitions.All(p => p.Leader >= 0)))
                return metadata.Sum(t => t.Partitions.Count);
            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException($"Kafka topics not ready on {bootstrapServers}: {string.Join(", ", metadata.Select(t => $"{t.Topic} {t.Error.Code}"))}.");
            await Task.Delay(100, ct);
        }
    }

    private void Run()
    {
        while (!stopping.IsCancellationRequested)
        {
            ConsumeResult<string, byte[]>? result;
            try
            {
                result = consumer!.Consume(PollTimeout);
            }
            catch (ConsumeException ex) when (!ex.Error.IsFatal)
            {
                continue;
            }
            if (result?.Message is not { } msg)
                continue;

            string type = "";
            string? correlationId = null;
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var header in msg.Headers ?? [])
            {
                var value = header.GetValueBytes() is { } bytes ? Encoding.UTF8.GetString(bytes) : "";
                switch (header.Key)
                {
                    case KafkaSender.TypeHeader: type = value; break;
                    case KafkaSender.CorrelationIdHeader: correlationId = value; break;
                    default: headers[header.Key] = value; break;
                }
            }
            Add(result.Topic, new KafkaMessage(result.Topic, type, msg.Key ?? "", correlationId, msg.Value ?? [], DateTimeOffset.UtcNow)
            {
                Headers = headers,
                Partition = result.Partition.Value,
                Offset = result.Offset.Value,
            });
        }
    }

    private void Add(string topic, KafkaMessage message)
    {
        TaskCompletionSource previous;
        lock (gate)
        {
            if (!byTopic.TryGetValue(topic, out var list))
                return;
            list.Add(message);
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

    private List<KafkaMessage> Topic(string topic) =>
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
