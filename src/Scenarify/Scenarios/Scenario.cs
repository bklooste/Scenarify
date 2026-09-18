using System.Net;
using System.Text.Json;

using Xunit.Sdk;

namespace Scenarify;

/// <summary>
/// A test as <b>Given* → When → Then+</b>. Build with <see cref="ScenarioExtensions.Scenario"/>, finish with <see cref="RunAsync"/>.
/// </summary>
/// <example>
/// <code>
/// fixture.Scenario()
///     .When(Http.Post("v1/bonus/{{brand}}", "cases/bonus/create.json"))
///     .ThenStatus(HttpStatusCode.Created)
///     .Capture("bonusId", "$.id")
///     .Then(Http.Get("v1/bonus/{{brand}}/{{bonusId}}").Eventually().Matches("cases/bonus/create.expected.json"))
///     .RunAsync();
/// </code>
/// </example>
public sealed class Scenario
{
    private readonly ServiceTestFixture fixture;
    private readonly List<Arrange> givens = [];
    private readonly List<IThenStep> thens = [];
    private Arrange? when;
    private Arrange? lastArrange;
    private string? caseName;
    private TimeSpan? timeout;

    internal Scenario(ServiceTestFixture fixture)
    {
        this.fixture = fixture;
        Variables = fixture.NewVariables();
    }

    /// <summary>The variables this scenario expands tokens from.</summary>
    public ScenarioVariables Variables { get; }

    /// <summary>Sets a variable for <c>{{name}}</c>.</summary>
    public Scenario With(string name, object? value)
    {
        Variables.Set(name, value);
        return this;
    }

    /// <summary>Names this case: used in snapshot file names for multi-row theories and in failure messages.</summary>
    public Scenario Snapshot(string name)
    {
        caseName = name;
        return this;
    }

    /// <summary>Overrides the timeout for eventual checks.</summary>
    public Scenario Within(TimeSpan value)
    {
        timeout = value;
        return this;
    }

    /// <summary>Adds an arrange step; runs in the order added, before the When.</summary>
    public Scenario Given(IGivenStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (when is not null)
            throw new InvalidOperationException("Given steps must come before When.");
        givens.Add(lastArrange = new Arrange(step.GivenAsync, step));
        return this;
    }

    /// <summary>Adds an ad-hoc arrange step.</summary>
    public Scenario Given(Func<ScenarioContext, Task> step) =>
        Given(new DelegateStep("given", step));

    /// <summary>Sets the trigger. A scenario has exactly one.</summary>
    public Scenario When(IWhenStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (when is not null)
            throw new InvalidOperationException("A scenario has exactly one When.");
        when = lastArrange = new Arrange(step.WhenAsync, step);
        return this;
    }

    /// <summary>Sets a trigger made of several steps run in order, e.g. two publishes.</summary>
    public Scenario When(IWhenStep first, params IWhenStep[] more)
    {
        ArgumentNullException.ThrowIfNull(first);
        if (more.Length == 0)
            return When(first);
        IWhenStep[] all = [first, .. more];
        return When(new DelegateStep(string.Join(" then ", all.Select(s => s.ToString())), async ctx =>
        {
            foreach (var step in all)
                await step.WhenAsync(ctx);
        }));
    }

    /// <summary>Sets a trigger made of several steps run at the same time (concurrency tests).</summary>
    public Scenario WhenConcurrently(params IWhenStep[] steps)
    {
        if (steps.Length < 2)
            throw new ArgumentException("WhenConcurrently needs at least two steps.", nameof(steps));
        return When(new DelegateStep("concurrently: " + string.Join(", ", steps.Select(s => s.ToString())),
            ctx => Task.WhenAll(steps.Select(s => s.WhenAsync(ctx)))));
    }

    /// <summary>Sets an ad-hoc trigger.</summary>
    public Scenario When(Func<ScenarioContext, Task> step) =>
        When(new DelegateStep("when", step));

    /// <summary>Adds a check; checks run in the order added, after the When.</summary>
    public Scenario Then(IThenStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        RequireWhen(nameof(Then));
        thens.Add(step);
        return this;
    }

    /// <summary>Adds an ad-hoc check.</summary>
    public Scenario Then(Func<ScenarioContext, Task> step) =>
        Then(new DelegateStep("then", step));

    /// <summary>The When's HTTP response has one of these statuses (instead of the default 2xx).</summary>
    public Scenario ThenStatus(params HttpStatusCode[] statuses)
    {
        WhenHttp(nameof(ThenStatus)).Status(statuses);
        return this;
    }

    /// <summary>The When's HTTP response body subset-matches <paramref name="expected"/>.</summary>
    public Scenario ThenBody(JsonSource expected)
    {
        WhenHttp(nameof(ThenBody)).Matches(expected);
        return this;
    }

    /// <summary>The When's HTTP response body equals <paramref name="expected"/>.</summary>
    public Scenario ThenBodyExactly(JsonSource expected)
    {
        WhenHttp(nameof(ThenBodyExactly)).MatchesExactly(expected);
        return this;
    }

    /// <summary>The When's HTTP response body matches its snapshot.</summary>
    public Scenario ThenBodySnapshot(params string[] only)
    {
        var step = WhenHttp(nameof(ThenBodySnapshot)).MatchesSnapshot();
        if (only.Length > 0)
            step.Only(only);
        return this;
    }

    /// <summary>The When's HTTP response body, deserialized, satisfies <paramref name="assertion"/>.</summary>
    public Scenario ThenBody<T>(Action<T> assertion)
    {
        WhenHttp(nameof(ThenBody)).Satisfies(assertion);
        return this;
    }

    /// <summary>
    /// Captures a value from the response of the most recent Given or When HTTP step into <c>{{name}}</c>,
    /// keeping its JSON type.
    /// </summary>
    public Scenario Capture(string name, string path) => AddCapture(name, path, required: true, fallback: null);

    /// <summary>
    /// Like <see cref="Capture(string, string)"/>, but uses <paramref name="fallback"/> when the path is absent
    /// (services that omit default values, such as a <c>0</c> id).
    /// </summary>
    public Scenario Capture(string name, string path, object? fallback) => AddCapture(name, path, required: false, fallback);

    private Scenario AddCapture(string name, string path, bool required, object? fallback)
    {
        var target = lastArrange ?? throw new InvalidOperationException("Capture must follow a Given or When HTTP step.");
        target.Captures.Add(new CaptureSpec(name, path, required, fallback));
        return this;
    }

    /// <summary>Runs the scenario.</summary>
    public async Task RunAsync()
    {
        if (when is null)
            throw new InvalidOperationException("A scenario needs a When.");
        if (thens.Count == 0 && when.Step is HttpStep { HasStatus: false, HasBodyCheck: false })
            throw new InvalidOperationException("A scenario needs at least one Then (or ThenStatus/ThenBody).");

        var context = new ScenarioContext(fixture, Variables, caseName, timeout ?? Eventually.DefaultTimeout);
        var phase = "Given";
        var index = 0;
        try
        {
            foreach (var given in givens)
            {
                index++;
                await given.RunAsync(context);
            }

            phase = "When";
            index = 1;
            await when.RunAsync(context);

            phase = "Then";
            index = 0;
            foreach (var then in thens)
            {
                index++;
                await then.ThenAsync(context);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var name = caseName is null ? "Scenario" : $"Scenario '{caseName}'";
            throw new XunitException($"{name} failed at {phase} #{index} ({Describe(phase, index)}).{Environment.NewLine}{ex.Message}", ex);
        }
    }

    private string Describe(string phase, int index) =>
        phase switch
        {
            "Given" => givens[index - 1].Step.ToString() ?? "",
            "When" => when!.Step.ToString() ?? "",
            _ => thens[index - 1].ToString() ?? "",
        };

    private HttpStep WhenHttp(string method)
    {
        RequireWhen(method);
        return when!.Step as HttpStep
            ?? throw new InvalidOperationException($"{method} needs an HTTP When step.");
    }

    private void RequireWhen(string method)
    {
        if (when is null)
            throw new InvalidOperationException($"{method} must come after When.");
    }

    private sealed record CaptureSpec(string Name, string Path, bool Required, object? Fallback);

    private sealed class Arrange(Func<ScenarioContext, Task> run, object step)
    {
        public object Step { get; } = step;

        public List<CaptureSpec> Captures { get; } = [];

        public async Task RunAsync(ScenarioContext context)
        {
            context.LastResponse = null;
            await run(context);
            if (Captures.Count == 0)
                return;

            var response = context.LastResponse
                ?? throw new InvalidOperationException($"Capture needs an HTTP response, but {Step} did not produce one.");
            foreach (var capture in Captures)
            {
                try
                {
                    var found = JsonPaths.Select(response.Json, capture.Path).Take(2).ToList();
                    if (found.Count == 0 && !capture.Required)
                        context.Variables.Set(capture.Name, capture.Fallback);
                    else
                        context.Variables.Set(capture.Name, JsonPaths.SelectSingle(response.Json, capture.Path));
                }
                catch (InvalidOperationException ex)
                {
                    throw new XunitException($"Capture '{capture.Name}' from {response.Describe()}: {ex.Message}");
                }
            }
        }
    }
}

/// <summary>Entry points for scenarios.</summary>
public static class ScenarioExtensions
{
    /// <summary>Starts a scenario against this fixture with fresh variables.</summary>
    public static Scenario Scenario(this ServiceTestFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return new Scenario(fixture);
    }

    /// <summary>Runs a case record.</summary>
    public static Task RunAsync(this ServiceTestFixture fixture, ScenarioCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        return testCase.ToScenario(fixture).RunAsync();
    }

    /// <summary>Reads a JSON value as <typeparamref name="T"/> with the default test options.</summary>
    public static T? As<T>(this HttpResult result) =>
        result.Json is null ? default : result.Json.Deserialize<T>(Json.DefaultOptions);
}
