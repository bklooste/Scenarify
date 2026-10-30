using System.Text.Json;

namespace Scenarify;

/// <summary>Kinesis Data Streams steps for scenarios; the fixture needs a <see cref="KinesisFeature"/>.</summary>
/// <remarks>
/// <b>No type or correlation-id filtering, by design.</b> A Kinesis record is just <c>(Data, PartitionKey)</c> plus a
/// server-assigned sequence number and arrival time; there is no message-type field, no correlation id and no headers.
/// The other Scenarify add-ons filter on those because their brokers carry them. Inventing an envelope here would change
/// the wire format from what a real Kinesis producer sends, so matching is on the JSON body and, optionally, the
/// partition key (<see cref="KinesisPublishedStep.WithKey"/>) only. Put a unique id in the body and match on it.
/// </remarks>
public static class Kinesis
{
    /// <summary>Put <paramref name="body"/> on <paramref name="stream"/>; <paramref name="partitionKey"/> expands tokens (a fresh GUID when null).</summary>
    public static KinesisPublishStep Publish(string stream, JsonSource body, string? partitionKey = null) =>
        new(stream, body, partitionKey);

    /// <summary>Then: a record whose body subset-matches <paramref name="expected"/> (and has <paramref name="partitionKey"/>, when given) was put.</summary>
    public static KinesisPublishedStep Published(string stream, JsonSource expected, string? partitionKey = null)
    {
        var step = new KinesisPublishedStep(stream, expected);
        return partitionKey is null ? step : step.WithKey(partitionKey);
    }

    /// <summary>
    /// Then: a record whose body subset-matches <paramref name="expected"/> and deserializes as <typeparamref name="T"/>
    /// was put. <typeparamref name="T"/> is a deserialization check only — there is no wire type to match.
    /// </summary>
    public static KinesisPublishedStep Published<T>(string stream, JsonSource expected) =>
        new KinesisPublishedStep(stream, expected).Satisfies<T>(_ => { });

    /// <summary>
    /// Then: no record whose body subset-matches <paramref name="expected"/> has been recorded. Checked once,
    /// immediately — put a barrier step before it that proves processing finished.
    /// </summary>
    public static DelegateStep NotPublished(string stream, JsonSource expected) =>
        new($"not published on {stream}", ctx =>
        {
            var resolved = expected.Resolve(ctx.Variables);
            var hits = ctx.Fixture.Feature<KinesisFeature>().Recorder.Messages(stream)
                .Where(m => JsonMatch.IsMatch(resolved, m.Json))
                .ToList();
            if (hits.Count > 0)
                throw new Xunit.Sdk.XunitException(
                    $"{hits.Count} record(s) on '{stream}' matched {resolved?.ToJsonString()}, expected none. First: {hits[0].Text}");
            return Task.CompletedTask;
        });
}

/// <summary>Puts one record; usable as a Given, the When, or a Then (for a marker that proves processing finished).</summary>
public sealed class KinesisPublishStep(string stream, JsonSource body, string? partitionKey) : IGivenStep, IWhenStep, IThenStep
{
    /// <inheritdoc />
    public async Task GivenAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var vars = context.Variables;
        await context.Fixture.Feature<KinesisFeature>().Sender.PublishJsonAsync(
            stream,
            body.ResolveText(vars),
            partitionKey is null ? null : vars.ExpandText(partitionKey),
            Xunit.TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public Task WhenAsync(ScenarioContext context) => GivenAsync(context);

    /// <inheritdoc />
    public Task ThenAsync(ScenarioContext context) => GivenAsync(context);

    /// <inheritdoc />
    public override string ToString() => $"put {body.Describe()} on {stream}";
}

/// <summary>Waits for a recorded record that matches; can capture values from it. As a Given it is a barrier.</summary>
public sealed class KinesisPublishedStep : IThenStep, IGivenStep
{
    private readonly string stream;
    private readonly JsonSource expected;
    private readonly List<(string Name, string Path)> captures = [];
    private readonly List<Action<KinesisRecordedMessage>> assertions = [];
    private JsonMatchOptions options = JsonMatchOptions.Subset;
    private TimeSpan? timeout;
    private string? keyFilter;

    internal KinesisPublishedStep(string stream, JsonSource expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        this.stream = stream;
        this.expected = expected;
    }

    /// <summary>Require an exact body match instead of a subset.</summary>
    public KinesisPublishedStep Exactly()
    {
        options = options with { Exact = true };
        return this;
    }

    /// <summary>Compare arrays in any order.</summary>
    public KinesisPublishedStep UnorderedArrays()
    {
        options = options with { UnorderedArrays = true };
        return this;
    }

    /// <summary>Also require the partition key to equal <paramref name="key"/> (tokens expand).</summary>
    public KinesisPublishedStep WithKey(string key)
    {
        keyFilter = key;
        return this;
    }

    /// <summary>After the match, assert on the record body deserialized as <typeparamref name="T"/>.</summary>
    public KinesisPublishedStep Satisfies<T>(Action<T> assertion, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        assertions.Add(m => assertion(m.As<T>(serializerOptions)
            ?? throw new Xunit.Sdk.XunitException($"Record on '{stream}' deserialized as null {typeof(T).Name}: {m.Text}")));
        return this;
    }

    /// <summary>Capture a value from the matched record body into <c>{{name}}</c>.</summary>
    public KinesisPublishedStep Capture(string name, string path)
    {
        captures.Add((name, path));
        return this;
    }

    /// <summary>Wait at most this long.</summary>
    public KinesisPublishedStep Within(TimeSpan value)
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
        var recorder = context.Fixture.Feature<KinesisFeature>().Recorder;
        var hit = await recorder.WaitForAsync(stream,
            m => (key is null || m.PartitionKey == key) && JsonMatch.IsMatch(resolved, m.Json, options),
            timeout ?? context.Timeout,
            $"{expected.Describe()} {resolved?.ToJsonString()}{(key is null ? "" : $" with key {key}")}",
            candidates => KinesisRecorder.ExplainNearest(resolved, candidates, options));

        foreach (var assertion in assertions)
            assertion(hit);
        foreach (var (name, path) in captures)
            vars.Set(name, JsonPaths.SelectSingle(hit.Json, path));
    }

    /// <inheritdoc />
    public Task GivenAsync(ScenarioContext context) => ThenAsync(context);

    /// <inheritdoc />
    public override string ToString() => $"published on {stream}";
}
