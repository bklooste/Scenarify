using System.Text.Json;

namespace Scenarify;

/// <summary>Service Bus steps for scenarios; the fixture needs a <see cref="ServiceBusFeature"/>.</summary>
public static class ServiceBus
{
    /// <summary>
    /// Send <paramref name="body"/> to queue or topic <paramref name="entity"/>. The subject is <paramref name="type"/>, or the
    /// CLR type of a <see cref="Json.From{T}"/> source. <paramref name="key"/> (partition key) and <paramref name="correlationId"/> expand tokens.
    /// </summary>
    /// <remarks><paramref name="headers"/> are application properties as <c>name=value&amp;name2={{token}}</c>.</remarks>
    public static ServiceBusSendStep Send(string entity, JsonSource body, string? type = null, string? key = null, string? correlationId = null,
        string? headers = null) =>
        new(entity, body, type, key, correlationId, headers);

    /// <summary>Then: a message of <typeparamref name="T"/> whose body subset-matches <paramref name="expected"/> was received.</summary>
    public static ServiceBusSentStep Sent<T>(string entity, JsonSource expected) =>
        new(entity, ServiceBusSender.TypeName(typeof(T)), expected);

    /// <summary>Then: a message of <paramref name="type"/> (any type when null) whose body subset-matches <paramref name="expected"/> was received.</summary>
    public static ServiceBusSentStep Sent(string entity, string? type, JsonSource expected) =>
        new(entity, type, expected);

    /// <summary>
    /// Then: no message of <paramref name="type"/> (any type when null) whose body subset-matches <paramref name="expected"/>
    /// has been recorded. Checked once, immediately — put a barrier step before it that proves processing finished.
    /// </summary>
    public static DelegateStep NotSent(string entity, string? type, JsonSource expected) =>
        new($"not sent {type ?? "any type"} on {entity}", ctx =>
        {
            var resolved = expected.Resolve(ctx.Variables);
            var hits = ctx.Fixture.Feature<ServiceBusFeature>().Recorder.Messages(entity, type)
                .Where(m => JsonMatch.IsMatch(resolved, m.Json))
                .ToList();
            if (hits.Count > 0)
                throw new Xunit.Sdk.XunitException(
                    $"{hits.Count} message(s) on '{entity}' matched {resolved?.ToJsonString()}, expected none. First: {hits[0].Text}");
            return Task.CompletedTask;
        });

    /// <summary>Then: no <typeparamref name="T"/> message whose body subset-matches <paramref name="expected"/> has been recorded.</summary>
    public static DelegateStep NotSent<T>(string entity, JsonSource expected) =>
        NotSent(entity, ServiceBusSender.TypeName(typeof(T)), expected);
}

/// <summary>Sends one message; usable as a Given, the When, or a Then (for a marker that proves processing finished).</summary>
public sealed class ServiceBusSendStep(string entity, JsonSource body, string? type, string? key, string? correlationId, string? headers = null)
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
                ? ServiceBusSender.TypeName(clr)
                : throw new InvalidOperationException($"Sending {body.Describe()} to '{entity}' needs a message type: pass type: \"...\" or use Json.From(obj).");

        await context.Fixture.Feature<ServiceBusFeature>().Sender.PublishJsonAsync(
            entity,
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
    public override string ToString() => $"send {body.Describe()} to {entity}";
}

/// <summary>Waits for a received message that matches; can capture values from it. As a Given it is a barrier.</summary>
public sealed class ServiceBusSentStep : IThenStep, IGivenStep
{
    private readonly string entity;
    private readonly string? type;
    private readonly JsonSource expected;
    private readonly List<(string Name, string Path)> captures = [];
    private readonly List<Action<ServiceBusRecordedMessage>> assertions = [];
    private JsonMatchOptions options = JsonMatchOptions.Subset;
    private TimeSpan? timeout;
    private string? keyFilter;
    private string? correlationFilter;

    internal ServiceBusSentStep(string entity, string? type, JsonSource expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        this.entity = entity;
        this.type = type;
        this.expected = expected;
    }

    /// <summary>Require an exact body match instead of a subset.</summary>
    public ServiceBusSentStep Exactly()
    {
        options = options with { Exact = true };
        return this;
    }

    /// <summary>Compare arrays in any order.</summary>
    public ServiceBusSentStep UnorderedArrays()
    {
        options = options with { UnorderedArrays = true };
        return this;
    }

    /// <summary>Also require the native partition key to equal <paramref name="key"/> (tokens expand).</summary>
    public ServiceBusSentStep WithKey(string key)
    {
        keyFilter = key;
        return this;
    }

    /// <summary>Also require the native correlation id to equal <paramref name="correlationId"/> (tokens expand).</summary>
    public ServiceBusSentStep WithCorrelation(string correlationId)
    {
        correlationFilter = correlationId;
        return this;
    }

    /// <summary>After the match, assert on the message deserialized as <typeparamref name="T"/>.</summary>
    public ServiceBusSentStep Satisfies<T>(Action<T> assertion, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        assertions.Add(m => assertion(m.As<T>(serializerOptions)!));
        return this;
    }

    /// <summary>Capture a value from the matched message body into <c>{{name}}</c>.</summary>
    public ServiceBusSentStep Capture(string name, string path)
    {
        captures.Add((name, path));
        return this;
    }

    /// <summary>Wait at most this long.</summary>
    public ServiceBusSentStep Within(TimeSpan value)
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
        var recorder = context.Fixture.Feature<ServiceBusFeature>().Recorder;
        var hit = await recorder.WaitForAsync(entity, type,
            m => (key is null || m.PartitionKey == key)
                && (correlation is null || m.CorrelationId == correlation)
                && JsonMatch.IsMatch(resolved, m.Json, options),
            timeout ?? context.Timeout,
            $"{expected.Describe()} {resolved?.ToJsonString()}",
            candidates => ServiceBusRecorder.ExplainNearest(resolved, candidates, options));

        foreach (var assertion in assertions)
            assertion(hit);
        foreach (var (name, path) in captures)
            vars.Set(name, JsonPaths.SelectSingle(hit.Json, path));
    }

    /// <inheritdoc />
    public Task GivenAsync(ScenarioContext context) => ThenAsync(context);

    /// <inheritdoc />
    public override string ToString() => $"sent {type ?? "any type"} on {entity}";
}
