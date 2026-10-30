using System.Text.Json;

namespace Scenarify;

/// <summary>Kafka steps for scenarios; the fixture needs a <see cref="KafkaFeature"/>.</summary>
public static class Kafka
{
    /// <summary>
    /// Produce <paramref name="body"/> to <paramref name="topic"/>. The message type is <paramref name="type"/>, or the CLR
    /// type of a <see cref="Json.From{T}"/> source. <paramref name="key"/> and <paramref name="correlationId"/> expand tokens.
    /// </summary>
    /// <remarks><paramref name="headers"/> are record headers as <c>name=value&amp;name2={{token}}</c>.</remarks>
    public static KafkaProduceStep Produce(string topic, JsonSource body, string? type = null, string? key = null, string? correlationId = null,
        string? headers = null) =>
        new(topic, body, type, key, correlationId, headers);

    /// <summary>Then: a message of <typeparamref name="T"/> whose body subset-matches <paramref name="expected"/> was produced.</summary>
    public static KafkaProducedStep Produced<T>(string topic, JsonSource expected) =>
        new(topic, KafkaSender.TypeName(typeof(T)), expected);

    /// <summary>
    /// Then: no message of <paramref name="type"/> (any type when null) whose body subset-matches <paramref name="expected"/>
    /// has been recorded. Checked once, immediately — put a barrier step before it that proves processing finished.
    /// </summary>
    public static DelegateStep NotProduced(string topic, string? type, JsonSource expected) =>
        new($"not produced {type ?? "any type"} on {topic}", ctx =>
        {
            var resolved = expected.Resolve(ctx.Variables);
            var hits = ctx.Fixture.Feature<KafkaFeature>().Recorder.Messages(topic, type)
                .Where(m => JsonMatch.IsMatch(resolved, m.Json))
                .ToList();
            if (hits.Count > 0)
                throw new Xunit.Sdk.XunitException(
                    $"{hits.Count} message(s) on '{topic}' matched {resolved?.ToJsonString()}, expected none. First: {hits[0].Text}");
            return Task.CompletedTask;
        });

    /// <summary>Then: no <typeparamref name="T"/> message whose body subset-matches <paramref name="expected"/> has been recorded.</summary>
    public static DelegateStep NotProduced<T>(string topic, JsonSource expected) =>
        NotProduced(topic, KafkaSender.TypeName(typeof(T)), expected);

    /// <summary>Then: a message of <paramref name="type"/> (any type when null) whose body subset-matches <paramref name="expected"/> was produced.</summary>
    public static KafkaProducedStep Produced(string topic, string? type, JsonSource expected) =>
        new(topic, type, expected);
}

/// <summary>Produces one record; usable as a Given, the When, or a Then (for a marker that proves processing finished).</summary>
public sealed class KafkaProduceStep(string topic, JsonSource body, string? type, string? key, string? correlationId, string? headers = null)
    : IGivenStep, IWhenStep, IThenStep
{
    /// <inheritdoc />
    public async Task GivenAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var vars = context.Variables;
        var messageType = type is not null
            ? vars.ExpandText(type)
            : body.ClrType is { } clr
                ? KafkaSender.TypeName(clr)
                : throw new InvalidOperationException($"Producing {body.Describe()} to '{topic}' needs a message type: pass type: \"...\" or use Json.From(obj).");

        await context.Fixture.Feature<KafkaFeature>().Sender.PublishJsonAsync(
            topic,
            body.ResolveText(vars),
            messageType,
            key is null ? null : vars.ExpandText(key),
            correlationId is null ? null : vars.ExpandText(correlationId),
            Xunit.TestContext.Current.CancellationToken,
            headers is null ? null : ParseHeaders(vars.ExpandText(headers)));
    }

    /// <inheritdoc />
    public Task WhenAsync(ScenarioContext context) => GivenAsync(context);

    /// <inheritdoc />
    public Task ThenAsync(ScenarioContext context) => GivenAsync(context);

    private static List<KeyValuePair<string, string>> ParseHeaders(string text) =>
        [.. text.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
        {
            var parts = pair.Split('=', 2);
            return new KeyValuePair<string, string>(parts[0], parts.Length > 1 ? parts[1] : "");
        })];

    /// <inheritdoc />
    public override string ToString() => $"produce {body.Describe()} to {topic}";
}

/// <summary>Waits for a produced record that matches; can capture values from it. As a Given it is a barrier.</summary>
public sealed class KafkaProducedStep : IThenStep, IGivenStep
{
    private readonly string topic;
    private readonly string? type;
    private readonly JsonSource expected;
    private readonly List<(string Name, string Path)> captures = [];
    private readonly List<Action<KafkaMessage>> assertions = [];
    private JsonMatchOptions options = JsonMatchOptions.Subset;
    private TimeSpan? timeout;
    private string? keyFilter;
    private string? correlationFilter;

    internal KafkaProducedStep(string topic, string? type, JsonSource expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        this.topic = topic;
        this.type = type;
        this.expected = expected;
    }

    /// <summary>Require an exact body match instead of a subset.</summary>
    public KafkaProducedStep Exactly()
    {
        options = options with { Exact = true };
        return this;
    }

    /// <summary>Compare arrays in any order.</summary>
    public KafkaProducedStep UnorderedArrays()
    {
        options = options with { UnorderedArrays = true };
        return this;
    }

    /// <summary>Also require the record key to equal <paramref name="key"/> (tokens expand).</summary>
    public KafkaProducedStep WithKey(string key)
    {
        keyFilter = key;
        return this;
    }

    /// <summary>Also require the correlation id to equal <paramref name="correlationId"/> (tokens expand).</summary>
    public KafkaProducedStep WithCorrelation(string correlationId)
    {
        correlationFilter = correlationId;
        return this;
    }

    /// <summary>After the match, assert on the message deserialized as <typeparamref name="T"/>.</summary>
    public KafkaProducedStep Satisfies<T>(Action<T> assertion, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        assertions.Add(m => assertion(m.As<T>(serializerOptions)!));
        return this;
    }

    /// <summary>Capture a value from the matched message body into <c>{{name}}</c>.</summary>
    public KafkaProducedStep Capture(string name, string path)
    {
        captures.Add((name, path));
        return this;
    }

    /// <summary>Wait at most this long.</summary>
    public KafkaProducedStep Within(TimeSpan value)
    {
        timeout = value;
        return this;
    }

    /// <inheritdoc />
    public async Task ThenAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var vars = context.Variables;
        var resolved = expected.Resolve(vars);
        var key = keyFilter is null ? null : vars.ExpandText(keyFilter);
        var correlation = correlationFilter is null ? null : vars.ExpandText(correlationFilter);
        var recorder = context.Fixture.Feature<KafkaFeature>().Recorder;
        var hit = await recorder.WaitForAsync(topic, type,
            m => (key is null || m.PartitionKey == key)
                && (correlation is null || m.CorrelationId == correlation)
                && JsonMatch.IsMatch(resolved, m.Json, options),
            timeout ?? context.Timeout,
            $"{expected.Describe()} {resolved?.ToJsonString()}",
            candidates => KafkaRecorder.ExplainNearest(resolved, candidates, options));

        foreach (var assertion in assertions)
            assertion(hit);
        foreach (var (name, path) in captures)
            vars.Set(name, JsonPaths.SelectSingle(hit.Json, path));
    }

    /// <inheritdoc />
    public Task GivenAsync(ScenarioContext context) => ThenAsync(context);

    /// <inheritdoc />
    public override string ToString() => $"produced {type ?? "any type"} on {topic}";
}
