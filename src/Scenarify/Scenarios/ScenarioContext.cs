using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace Scenarify;

/// <summary>A step that can arrange state before the trigger.</summary>
public interface IGivenStep
{
    /// <summary>Runs the step.</summary>
    Task GivenAsync(ScenarioContext context);
}

/// <summary>The trigger of a scenario.</summary>
public interface IWhenStep
{
    /// <summary>Runs the step.</summary>
    Task WhenAsync(ScenarioContext context);
}

/// <summary>A check run after the trigger.</summary>
public interface IThenStep
{
    /// <summary>Runs the step; throws when the check fails.</summary>
    Task ThenAsync(ScenarioContext context);
}

/// <summary>An HTTP response as a scenario sees it.</summary>
public sealed record HttpResult(string Request, HttpStatusCode Status, string Body)
{
    /// <summary>Response and content headers (names case-insensitive).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private JsonNode? json;
    private bool parsed;

    /// <summary>The body parsed as JSON; throws with the raw body when it is not JSON.</summary>
    public JsonNode? Json
    {
        get
        {
            if (!parsed)
            {
                json = JsonMatch.ParseActual(Body);
                parsed = true;
            }
            return json;
        }
    }

    /// <summary>A one-line description for failure messages.</summary>
    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture, $"{Request} -> {(int)Status} {Status}");
}

/// <summary>State shared by the steps of one scenario run.</summary>
public sealed class ScenarioContext(ServiceTestFixture fixture, ScenarioVariables variables, string? caseName, TimeSpan timeout)
{
    private int snapshotCount;

    /// <summary>The fixture the scenario runs against.</summary>
    public ServiceTestFixture Fixture { get; } = fixture;

    /// <summary>The scenario's variables; steps may add to them.</summary>
    public ScenarioVariables Variables { get; } = variables;

    /// <summary>The case name used for snapshot file names, when the test supplies one.</summary>
    public string? CaseName { get; } = caseName;

    /// <summary>The timeout for eventual checks.</summary>
    public TimeSpan Timeout { get; } = timeout;

    /// <summary>The most recent HTTP response from a Given or When step.</summary>
    public HttpResult? LastResponse { get; set; }

    /// <summary>The next snapshot name for this scenario: the second and later get a numeric suffix.</summary>
    public string NextSnapshotName(string? explicitName)
    {
        var count = Interlocked.Increment(ref snapshotCount);
        var name = explicitName ?? JsonSnapshot.NameForCurrentTest(CaseName);
        return count == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name}.{count}");
    }

    /// <summary>Writes to test output.</summary>
    public void Log(string message) => Fixture.Log(message);
}
