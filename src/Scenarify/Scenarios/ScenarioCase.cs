using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;
using Xunit.Sdk;

namespace Scenarify;

/// <summary>
/// Base for case records: a whole test as data. Each case shows up and reruns as its own test (it is an
/// xUnit serializable row) and displays as its <c>Name</c>. <see cref="ToScenario"/> turns it into a fluent
/// scenario, so a case that needs one extra step can grow without starting over.
/// </summary>
/// <remarks>
/// Derived records need a public parameterless constructor for xUnit to rebuild them. String fields that
/// hold JSON are file paths or inline JSON (see <see cref="JsonSource"/>).
/// </remarks>
#pragma warning disable xUnit3001 // abstract: the concrete records supply the parameterless constructor
public abstract record ScenarioCase : IXunitSerializable
#pragma warning restore xUnit3001
{
    private const string SerializedKey = "case";

    /// <summary>Builds the scenario this case describes.</summary>
    public abstract Scenario ToScenario(ServiceTestFixture fixture);

    /// <summary>The case name (the derived record's <c>Name</c>).</summary>
    protected string CaseName =>
        GetType().GetProperty("Name")?.GetValue(this) as string ?? GetType().Name;

    /// <inheritdoc />
    public sealed override string ToString() => CaseName;

    /// <inheritdoc />
    public void Serialize(IXunitSerializationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        info.AddValue(SerializedKey, JsonSerializer.Serialize(this, GetType(), Json.DefaultOptions));
    }

    /// <inheritdoc />
    public void Deserialize(IXunitSerializationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var json = info.GetValue<string>(SerializedKey)
            ?? throw new InvalidOperationException("Serialized case is missing.");
        var copy = JsonSerializer.Deserialize(json, GetType(), Json.DefaultOptions)
            ?? throw new InvalidOperationException($"Serialized case did not deserialize to {GetType().Name}.");

        // Init-only properties are settable through reflection; this copies the rebuilt case into this instance.
        foreach (var property in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanWrite && property.GetIndexParameters().Length == 0)
                property.SetValue(this, property.GetValue(copy));
        }
    }

    /// <summary>A scenario with this case's name set for snapshots and its mocks loaded.</summary>
    protected Scenario Start(ServiceTestFixture fixture, IEnumerable<string>? mocks)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var scenario = fixture.Scenario().Snapshot(CaseName);
        foreach (var mock in mocks ?? [])
            scenario.Given(Mock.Expectations(mock));
        return scenario;
    }

    /// <summary>Applies <paramref name="expected"/>, or the case's snapshot when it is null or empty.</summary>
    protected static HttpStep Expect(HttpStep step, string? expected) =>
        string.IsNullOrWhiteSpace(expected) ? step.MatchesSnapshot() : step.Matches(expected);

    /// <summary>True for 2xx.</summary>
    protected static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;
}

/// <summary>POST, then GET until the result matches. A non-2xx <c>Status</c> checks <c>Expected</c> against the POST response instead.</summary>
public sealed record PostThenGet(string Name, string PostPath, string Body, string GetPath, string? Expected,
    HttpStatusCode Status = HttpStatusCode.OK, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public PostThenGet() : this("", "", "{}", "", null) { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture)
    {
        var post = Http.Post(PostPath, Body).Status(Status);
        var scenario = Start(fixture, Mocks);
        if (!IsSuccess(Status))
            return scenario.When(Expect(post, Expected));
        return scenario.When(post).Then(Expect(Http.Get(GetPath).Eventually(), Expected));
    }
}

/// <summary>POST and check the response only: its status, and its body when <c>Expected</c> is given.</summary>
public sealed record PostThenResponse(string Name, string PostPath, string Body, HttpStatusCode Status, string? Expected = null,
    string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public PostThenResponse() : this("", "", "{}", HttpStatusCode.OK) { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture)
    {
        var post = Http.Post(PostPath, Body).Status(Status);
        return Start(fixture, Mocks).When(string.IsNullOrWhiteSpace(Expected) ? post : post.Matches(Expected));
    }
}

/// <summary>POST, then MockServer eventually receives a request (with a body subset-matching <c>Expected</c> when given).</summary>
public sealed record PostThenMockReceived(string Name, string PostPath, string Body, string Method, string MockPath, string? Expected = null,
    HttpStatusCode Status = HttpStatusCode.OK, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public PostThenMockReceived() : this("", "", "{}", "POST", "") { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Http.Post(PostPath, Body).Status(Status))
            .Then(Mock.Received(Method, MockPath, string.IsNullOrWhiteSpace(Expected) ? null : Json.Parse(Expected)));
}

/// <summary>Loads rows of a case record from one JSON array file (for tests with many near-identical rows).</summary>
public static class JsonCases
{
    /// <summary>
    /// Reads <paramref name="file"/> (an array of objects, each with a <c>name</c>) as theory rows.
    /// </summary>
    public static TheoryData<T> Load<T>(string file)
    {
        var node = Json.File(file).Resolve() as JsonArray
            ?? throw new InvalidOperationException($"{file} must be a JSON array of cases.");
        var data = new TheoryData<T>();
        var index = 0;
        foreach (var item in node)
        {
            index++;
            if (item is not JsonObject obj || obj["name"] is null)
                throw new InvalidOperationException($"{file} case #{index} needs a \"name\".");
            data.Add(item.Deserialize<T>(Json.DefaultOptions)!);
        }
        return data;
    }
}
