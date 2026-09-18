using System.Text.Json;

namespace Scenarify;

/// <summary>Stream steps for scenarios; the fixture needs a <see cref="RedisFeature"/>.</summary>
public static class Streams
{
    /// <summary>
    /// Publish <paramref name="body"/> to <paramref name="topic"/>. The message type is <paramref name="type"/>, or the CLR
    /// type of a <see cref="Json.From{T}"/> source. <paramref name="key"/> and <paramref name="correlationId"/> expand tokens.
    /// </summary>
    /// <remarks><paramref name="headers"/> are message headers as <c>name=value&amp;name2={{token}}</c>.</remarks>
    public static StreamPublishStep Publish(string topic, JsonSource body, string? type = null, string? key = null, string? correlationId = null,
        string? headers = null) =>
        new(topic, body, type, key, correlationId, headers);

    /// <summary>Then: a message of <typeparamref name="T"/> whose body subset-matches <paramref name="expected"/> was published.</summary>
    public static StreamPublishedStep Published<T>(string topic, JsonSource expected) =>
        new(topic, StreamSender.TypeName(typeof(T)), expected);

    /// <summary>
    /// Then: no message of <paramref name="type"/> (any type when null) whose body subset-matches <paramref name="expected"/>
    /// has been recorded. Checked once, immediately — put a barrier step before it that proves processing finished.
    /// </summary>
    public static DelegateStep NotPublished(string topic, string? type, JsonSource expected) =>
        new($"not published {type ?? "any type"} on {topic}", ctx =>
        {
            var resolved = expected.Resolve(ctx.Variables);
            var hits = ctx.Fixture.Feature<RedisFeature>().Recorder.Messages(topic, type)
                .Where(m => JsonMatch.IsMatch(resolved, m.Json))
                .ToList();
            if (hits.Count > 0)
                throw new Xunit.Sdk.XunitException(
                    $"{hits.Count} message(s) on '{topic}' matched {resolved?.ToJsonString()}, expected none. First: {hits[0].Text}");
            return Task.CompletedTask;
        });

    /// <summary>Then: no <typeparamref name="T"/> message whose body subset-matches <paramref name="expected"/> has been recorded.</summary>
    public static DelegateStep NotPublished<T>(string topic, JsonSource expected) =>
        NotPublished(topic, StreamSender.TypeName(typeof(T)), expected);

    /// <summary>Then: a message of <paramref name="type"/> (any type when null) whose body subset-matches <paramref name="expected"/> was published.</summary>
    public static StreamPublishedStep Published(string topic, string? type, JsonSource expected) =>
        new(topic, type, expected);
}

/// <summary>Publishes one message; usable as a Given, the When, or a Then (for a marker that proves processing finished).</summary>
public sealed class StreamPublishStep(string topic, JsonSource body, string? type, string? key, string? correlationId, string? headers = null)
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
                ? StreamSender.TypeName(clr)
                : throw new InvalidOperationException($"Publishing {body.Describe()} to '{topic}' needs a message type: pass type: \"...\" or use Json.From(obj).");

        await context.Fixture.Feature<RedisFeature>().Sender.PublishJsonAsync(
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
    public override string ToString() => $"publish {body.Describe()} to {topic}";
}

/// <summary>Waits for a published message that matches; can capture values from it. As a Given it is a barrier.</summary>
public sealed class StreamPublishedStep : IThenStep, IGivenStep
{
    private readonly string topic;
    private readonly string? type;
    private readonly JsonSource expected;
    private readonly List<(string Name, string Path)> captures = [];
    private readonly List<Action<RecordedMessage>> assertions = [];
    private JsonMatchOptions options = JsonMatchOptions.Subset;
    private TimeSpan? timeout;
    private string? keyFilter;
    private string? correlationFilter;

    internal StreamPublishedStep(string topic, string? type, JsonSource expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        this.topic = topic;
        this.type = type;
        this.expected = expected;
    }

    /// <summary>Require an exact body match instead of a subset.</summary>
    public StreamPublishedStep Exactly()
    {
        options = options with { Exact = true };
        return this;
    }

    /// <summary>Compare arrays in any order.</summary>
    public StreamPublishedStep UnorderedArrays()
    {
        options = options with { UnorderedArrays = true };
        return this;
    }

    /// <summary>Also require the partition key to equal <paramref name="key"/> (tokens expand).</summary>
    public StreamPublishedStep WithKey(string key)
    {
        keyFilter = key;
        return this;
    }

    /// <summary>Also require the correlation id to equal <paramref name="correlationId"/> (tokens expand).</summary>
    public StreamPublishedStep WithCorrelation(string correlationId)
    {
        correlationFilter = correlationId;
        return this;
    }

    /// <summary>After the match, assert on the message deserialized as <typeparamref name="T"/>.</summary>
    public StreamPublishedStep Satisfies<T>(Action<T> assertion, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        assertions.Add(m => assertion(m.As<T>(serializerOptions)!));
        return this;
    }

    /// <summary>Capture a value from the matched message body into <c>{{name}}</c>.</summary>
    public StreamPublishedStep Capture(string name, string path)
    {
        captures.Add((name, path));
        return this;
    }

    /// <summary>Wait at most this long.</summary>
    public StreamPublishedStep Within(TimeSpan value)
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
        var recorder = context.Fixture.Feature<RedisFeature>().Recorder;
        var hit = await recorder.WaitForAsync(topic, type,
            m => (key is null || m.PartitionKey == key)
                && (correlation is null || m.CorrelationId == correlation)
                && JsonMatch.IsMatch(resolved, m.Json, options),
            timeout ?? context.Timeout,
            $"{expected.Describe()} {resolved?.ToJsonString()}",
            candidates => StreamRecorder.ExplainNearest(resolved, candidates, options));

        foreach (var assertion in assertions)
            assertion(hit);
        foreach (var (name, path) in captures)
            vars.Set(name, JsonPaths.SelectSingle(hit.Json, path));
    }

    /// <inheritdoc />
    public Task GivenAsync(ScenarioContext context) => ThenAsync(context);

    /// <inheritdoc />
    public override string ToString() => $"published {type ?? "any type"} on {topic}";
}
