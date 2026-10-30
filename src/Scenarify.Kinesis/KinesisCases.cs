using System.Net;

namespace Scenarify;

/// <summary>Put a record, then GET until the result matches (or matches the case snapshot when <c>Expected</c> is empty).</summary>
public sealed record KinesisPublishThenGet(string Name, string Stream, string Event, string GetPath, string? Expected,
    string? Key = null, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public KinesisPublishThenGet() : this("", "", "{}", "", null) { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Kinesis.Publish(Stream, Event, Key))
            .Then(Expect(Http.Get(GetPath).Eventually(), Expected));
}

/// <summary>POST, then a matching record is put on the stream.</summary>
public sealed record KinesisPostThenPublished(string Name, string PostPath, string Body, string Stream, string Expected,
    HttpStatusCode Status = HttpStatusCode.OK, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public KinesisPostThenPublished() : this("", "", "{}", "", "{}") { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Http.Post(PostPath, Body).Status(Status))
            .Then(Kinesis.Published(Stream, Expected));
}

/// <summary>Put a record, then MockServer eventually receives a request (body subset-matching <c>Expected</c> when given).</summary>
public sealed record KinesisPublishThenMockReceived(string Name, string Stream, string Event, string Method, string MockPath,
    string? Expected = null, string? Key = null, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public KinesisPublishThenMockReceived() : this("", "", "{}", "POST", "") { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Kinesis.Publish(Stream, Event, Key))
            .Then(Mock.Received(Method, MockPath, string.IsNullOrWhiteSpace(Expected) ? null : Json.Parse(Expected)));
}
