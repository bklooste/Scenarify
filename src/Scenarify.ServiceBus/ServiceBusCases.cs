using System.Net;

namespace Scenarify;

/// <summary>Send a message, then GET until the result matches (or matches the case snapshot when <c>Expected</c> is empty).</summary>
public sealed record SendThenGet(string Name, string Entity, string Type, string Message, string GetPath, string? Expected,
    string? Key = null, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public SendThenGet() : this("", "", "", "{}", "", null) { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(ServiceBus.Send(Entity, Message, Type, Key))
            .Then(Expect(Http.Get(GetPath).Eventually(), Expected));
}

/// <summary>POST, then a matching message is received on a queue or subscription.</summary>
public sealed record PostThenSent(string Name, string PostPath, string Body, string Entity, string Type, string Expected,
    HttpStatusCode Status = HttpStatusCode.OK, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public PostThenSent() : this("", "", "{}", "", "", "{}") { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(Http.Post(PostPath, Body).Status(Status))
            .Then(ServiceBus.Sent(Entity, Type, Expected));
}

/// <summary>Send a message, then MockServer eventually receives a request (body subset-matching <c>Expected</c> when given).</summary>
public sealed record SendThenMockReceived(string Name, string Entity, string Type, string Message, string Method, string MockPath,
    string? Expected = null, string? Key = null, string[]? Mocks = null) : ScenarioCase
{
    /// <summary>For xUnit deserialization.</summary>
    public SendThenMockReceived() : this("", "", "", "{}", "POST", "") { }

    /// <inheritdoc />
    public override Scenario ToScenario(ServiceTestFixture fixture) =>
        Start(fixture, Mocks)
            .When(ServiceBus.Send(Entity, Message, Type, Key))
            .Then(Mock.Received(Method, MockPath, string.IsNullOrWhiteSpace(Expected) ? null : Json.Parse(Expected)));
}
