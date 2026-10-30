using System.Text.Json;

namespace Scenarify;

/// <summary>Event Hubs steps for scenarios; the fixture needs a <see cref="EventHubsFeature"/>.</summary>
public static class EventHubs
{
    /// <summary>
    /// Publish <paramref name="body"/> to Event Hub <paramref name="topic"/>. The message type is <paramref name="type"/>, or the CLR
    /// type of a <see cref="Json.From{T}"/> source. <paramref name="key"/> and <paramref name="correlationId"/> expand tokens.
    /// </summary>
    /// <remarks><paramref name="headers"/> become application properties, as <c>name=value&amp;name2={{token}}</c>.</remarks>
    public static EventHubsPublishStep Publish(string topic, JsonSource body, string? type = null, string? key = null, string? correlationId = null,
        string? headers = null) =>
        new(topic, body, type, key, correlationId, headers);

    /// <summary>Then: a message of <typeparamref name="T"/> whose body subset-matches <paramref name="expected"/> was published.</summary>
    public static EventHubsPublishedStep Published<T>(string topic, JsonSource expected) =>
        new(topic, EventHubsSender.TypeName(typeof(T)), expected);

    /// <summary>
    /// Then: no message of <paramref name="type"/> (any type when null) whose body subset-matches <paramref name="expected"/>
    /// has been recorded. Checked once, immediately — put a barrier step before it that proves processing finished.
    /// </summary>
    public static DelegateStep NotPublished(string topic, string? type, JsonSource expected) =>
        new($"not published {type ?? "any type"} on {topic}", ctx =>
        {
            var resolved = expected.Resolve(ctx.Variables);
            var hits = ctx.Fixture.Feature<EventHubsFeature>().Recorder.Messages(topic, type)
                .Where(m => JsonMatch.IsMatch(resolved, m.Json))
                .ToList();
            if (hits.Count > 0)
                throw new Xunit.Sdk.XunitException(
                    $"{hits.Count} message(s) on '{topic}' matched {resolved?.ToJsonString()}, expected none. First: {hits[0].Text}");
            return Task.CompletedTask;
        });

    /// <summary>Then: no <typeparamref name="T"/> message whose body subset-matches <paramref name="expected"/> has been recorded.</summary>
    public static DelegateStep NotPublished<T>(string topic, JsonSource expected) =>
        NotPublished(topic, EventHubsSender.TypeName(typeof(T)), expected);

    /// <summary>Then: a message of <paramref name="type"/> (any type when null) whose body subset-matches <paramref name="expected"/> was published.</summary>
    public static EventHubsPublishedStep Published(string topic, string? type, JsonSource expected) =>
        new(topic, type, expected);
}

/// <summary>Publishes one message; usable as a Given, the When, or a Then (for a marker that proves processing finished).</summary>
public sealed class EventHubsPublishStep(string topic, JsonSource body, string? type, string? key, string? correlationId, string? headers = null)
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
                ? EventHubsSender.TypeName(clr)
                : throw new InvalidOperationException($"Publishing {body.Describe()} to '{topic}' needs a message type: pass type: \"...\" or use Json.From(obj).");

        await context.Fixture.Feature<EventHubsFeature>().Sender.PublishJsonAsync(
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
public sealed class EventHubsPublishedStep : IThenStep, IGivenStep
{
    private readonly string topic;
    private readonly string? type;
    private readonly JsonSource expected;
    private readonly List<(string Name, string Path)> captures = [];
    private readonly List<Action<EventHubsMessage>> assertions = [];
    private JsonMatchOptions options = JsonMatchOptions.Subset;
    private TimeSpan? timeout;
    private string? keyFilter;
    private string? correlationFilter;

    internal EventHubsPublishedStep(string topic, string? type, JsonSource expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        this.topic = topic;
        this.type = type;
        this.expected = expected;
    }

    /// <summary>Require an exact body match instead of a subset.</summary>
    public EventHubsPublishedStep Exactly()
    {
        options = options with { Exact = true };
        return this;
    }

    /// <summary>Compare arrays in any order.</summary>
    public EventHubsPublishedStep UnorderedArrays()
    {
        options = options with { UnorderedArrays = true };
        return this;
    }

    /// <summary>Also require the partition key to equal <paramref name="key"/> (tokens expand).</summary>
    public EventHubsPublishedStep WithKey(string key)
    {
        keyFilter = key;
        return this;
    }

    /// <summary>Also require the native <c>CorrelationId</c> to equal <paramref name="correlationId"/> (tokens expand).</summary>
    public EventHubsPublishedStep WithCorrelation(string correlationId)
    {
        correlationFilter = correlationId;
        return this;
    }

    /// <summary>After the match, assert on the message deserialized as <typeparamref name="T"/>.</summary>
    public EventHubsPublishedStep Satisfies<T>(Action<T> assertion, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        assertions.Add(m => assertion(m.As<T>(serializerOptions)!));
        return this;
    }

    /// <summary>Capture a value from the matched message body into <c>{{name}}</c>.</summary>
    public EventHubsPublishedStep Capture(string name, string path)
    {
        captures.Add((name, path));
        return this;
    }

    /// <summary>Wait at most this long.</summary>
    public EventHubsPublishedStep Within(TimeSpan value)
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
        var recorder = context.Fixture.Feature<EventHubsFeature>().Recorder;
        var hit = await recorder.WaitForAsync(topic, type,
            m => (key is null || m.PartitionKey == key)
                && (correlation is null || m.CorrelationId == correlation)
                && JsonMatch.IsMatch(resolved, m.Json, options),
            timeout ?? context.Timeout,
            $"{expected.Describe()} {resolved?.ToJsonString()}",
            candidates => EventHubsRecorder.ExplainNearest(resolved, candidates, options));

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
