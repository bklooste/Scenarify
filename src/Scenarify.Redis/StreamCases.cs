using System.Net;

namespace Scenarify;

/// <summary>Publish an event, then GET until the result matches (or matches the case snapshot when <c>Expected</c> is empty).</summary>
public sealed record PublishThenGet(string Name, string Topic, string Type, string Event, string GetPath, string? Expected,
    string? Key = null, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public PublishThenGet() : this("", "", "", "{}", "", null) { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Streams.Publish(Topic, Event, Type, Key))
            .Then(Expect(Http.Get(GetPath).Eventually(), Expected));
}

/// <summary>POST, then a matching event is published.</summary>
public sealed record PostThenPublished(string Name, string PostPath, string Body, string Topic, string Type, string Expected,
    HttpStatusCode Status = HttpStatusCode.OK, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public PostThenPublished() : this("", "", "{}", "", "", "{}") { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Http.Post(PostPath, Body).Status(Status))
            .Then(Streams.Published(Topic, Type, Expected));
}

/// <summary>Publish an event, then MockServer eventually receives a request (body subset-matching <c>Expected</c> when given).</summary>
public sealed record PublishThenMockReceived(string Name, string Topic, string Type, string Event, string Method, string MockPath,
    string? Expected = null, string? Key = null, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public PublishThenMockReceived() : this("", "", "", "{}", "POST", "") { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Streams.Publish(Topic, Event, Type, Key))
            .Then(Mock.Received(Method, MockPath, string.IsNullOrWhiteSpace(Expected) ? null : Json.Parse(Expected)));
}
