using System.Net;

namespace Scenarify;

/// <summary>Publish an event, then GET until the result matches (or matches the case snapshot when <c>Expected</c> is empty).</summary>
public sealed record EventHubsPublishThenGet(string Name, string Topic, string Type, string Event, string GetPath, string? Expected,
    string? Key = null, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public EventHubsPublishThenGet() : this("", "", "", "{}", "", null) { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(EventHubs.Publish(Topic, Event, Type, Key))
            .Then(Expect(Http.Get(GetPath).Eventually(), Expected));
}

/// <summary>POST, then a matching event is published.</summary>
public sealed record EventHubsPostThenPublished(string Name, string PostPath, string Body, string Topic, string Type, string Expected,
    HttpStatusCode Status = HttpStatusCode.OK, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public EventHubsPostThenPublished() : this("", "", "{}", "", "", "{}") { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Http.Post(PostPath, Body).Status(Status))
            .Then(EventHubs.Published(Topic, Type, Expected));
}

/// <summary>Publish an event, then MockServer eventually receives a request (body subset-matching <c>Expected</c> when given).</summary>
public sealed record EventHubsPublishThenMockReceived(string Name, string Topic, string Type, string Event, string Method, string MockPath,
    string? Expected = null, string? Key = null, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public EventHubsPublishThenMockReceived() : this("", "", "", "{}", "POST", "") { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(EventHubs.Publish(Topic, Event, Type, Key))
            .Then(Mock.Received(Method, MockPath, string.IsNullOrWhiteSpace(Expected) ? null : Json.Parse(Expected)));
}
