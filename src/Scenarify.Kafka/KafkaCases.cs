using System.Net;

namespace Scenarify;

/// <summary>Produce a record, then GET until the result matches (or matches the case snapshot when <c>Expected</c> is empty).</summary>
public sealed record ProduceThenGet(string Name, string Topic, string Type, string Event, string GetPath, string? Expected,
    string? Key = null, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public ProduceThenGet() : this("", "", "", "{}", "", null) { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Kafka.Produce(Topic, Event, Type, Key))
            .Then(Expect(Http.Get(GetPath).Eventually(), Expected));
}

/// <summary>POST, then a matching record is produced.</summary>
public sealed record PostThenProduced(string Name, string PostPath, string Body, string Topic, string Type, string Expected,
    HttpStatusCode Status = HttpStatusCode.OK, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public PostThenProduced() : this("", "", "{}", "", "", "{}") { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Http.Post(PostPath, Body).Status(Status))
            .Then(Kafka.Produced(Topic, Type, Expected));
}

/// <summary>Produce a record, then MockServer eventually receives a request (body subset-matching <c>Expected</c> when given).</summary>
public sealed record ProduceThenMockReceived(string Name, string Topic, string Type, string Event, string Method, string MockPath,
    string? Expected = null, string? Key = null, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public ProduceThenMockReceived() : this("", "", "", "{}", "POST", "") { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Kafka.Produce(Topic, Event, Type, Key))
            .Then(Mock.Received(Method, MockPath, string.IsNullOrWhiteSpace(Expected) ? null : Json.Parse(Expected)));
}
