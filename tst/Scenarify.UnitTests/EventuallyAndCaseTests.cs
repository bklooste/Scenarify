using System.Net;

using Xunit.Sdk;

namespace Scenarify.UnitTests;

[Trait("TestType", "UnitTest")]
public class EventuallyAndCaseTests
{
    [Fact]
    public async Task eventually_returns_once_the_assertion_passes()
    {
        var calls = 0;
        var result = await Eventually.Assert(() =>
        {
            calls++;
            Assert.True(calls >= 3);
            return Task.FromResult(calls);
        }, TimeSpan.FromSeconds(5));
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task eventually_reports_the_last_failure()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<EventuallyTimeoutException>(() => Eventually.Assert(() =>
        {
            calls++;
            throw new InvalidOperationException($"attempt {calls} failed");
        }, TimeSpan.FromMilliseconds(300), "balance updated"));

        Assert.Contains("'balance updated' still failing", ex.Message);
        Assert.Contains($"attempt {calls} failed", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task eventually_true_explains_false()
    {
        var ex = await Assert.ThrowsAsync<EventuallyTimeoutException>(() =>
            Eventually.True(() => Task.FromResult(false), TimeSpan.FromMilliseconds(100), "ready"));
        Assert.Contains("ready returned false", ex.Message);
    }

    [Fact]
    public void case_records_round_trip_through_xunit_serialization()
    {
        var original = new PostThenGet("create", "v1/x", """{ "a": 1 }""", "v1/x/{{customerId}}", "cases/x.expected.json",
            HttpStatusCode.Created, ["mockserver/a.json"]);

        var info = new FakeSerializationInfo();
        original.Serialize(info);
        var copy = new PostThenGet();
        copy.Deserialize(info);

        Assert.Equal(original.Name, copy.Name);
        Assert.Equal(original.Body, copy.Body);
        Assert.Equal(HttpStatusCode.Created, copy.Status);
        Assert.Equal(original.Mocks, copy.Mocks);
        Assert.Equal("create", copy.ToString());
    }

    [Fact]
    public void json_cases_load_named_rows()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "cases", "unit");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "rows.cases.json"), """
            [
              { "name": "a", "postPath": "v1/a", "body": "{}", "status": "BadRequest" },
              { "name": "b", "postPath": "v1/b", "body": "{}", "status": 200 }
            ]
            """);

        var rows = JsonCases.Load<PostThenResponse>("cases/unit/rows.cases.json").Select(r => r.Data).ToList();

        Assert.Equal(["a", "b"], rows.Select(r => r.Name));
        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.OK], rows.Select(r => r.Status));
    }

    [Fact]
    public async Task ensure_once_runs_setup_once_and_caches_failure()
    {
        var fixture = new UnstartedFixture();
        var runs = 0;
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => fixture.EnsureOnceAsync("k", async () =>
        {
            Interlocked.Increment(ref runs);
            await Task.Delay(20);
        })));
        Assert.Equal(1, runs);

        var id = await fixture.EnsureOnceAsync("id", () => Task.FromResult(Guid.NewGuid()));
        Assert.Equal(id, await fixture.EnsureOnceAsync("id", () => Task.FromResult(Guid.NewGuid())));

        var failures = 0;
        Task Fail() => fixture.EnsureOnceAsync("bad", () =>
        {
            failures++;
            throw new InvalidOperationException("seed failed");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(Fail);
        await Assert.ThrowsAsync<InvalidOperationException>(Fail);
        Assert.Equal(1, failures);
    }

    [Fact]
    public void scenario_builder_rejects_misuse()
    {
        var fixture = new UnstartedFixture();
        Assert.Throws<InvalidOperationException>(() => fixture.Scenario().Then(Http.Get("x")));
        Assert.Throws<InvalidOperationException>(() => fixture.Scenario().When(Http.Get("x")).When(Http.Get("y")));
        Assert.Throws<InvalidOperationException>(() => fixture.Scenario().When(Http.Get("x")).Given(Mock.Get("y")));
        Assert.Throws<InvalidOperationException>(() => fixture.Scenario().Capture("id", "$.id"));
        Assert.Throws<InvalidOperationException>(() => Http.Get("x").MatchesSnapshot().MatchesSnapshot());
        Assert.Throws<InvalidOperationException>(() => Http.Get("x").Only("a"));
    }

    [Fact]
    public async Task scenario_without_a_check_is_rejected()
    {
        var fixture = new UnstartedFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scenario().When(Http.Post("x")).RunAsync());
    }

    [Fact]
    public async Task a_delegate_when_that_asserts_needs_no_then()
    {
        var ran = false;
        await new UnstartedFixture().Scenario().When(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        }).RunAsync();
        Assert.True(ran);
    }

    [Fact]
    public async Task scenario_failure_names_the_phase_and_step()
    {
        var fixture = new UnstartedFixture();
        var ex = await Assert.ThrowsAsync<XunitException>(() => fixture.Scenario()
            .When(_ => Task.CompletedTask)
            .Then(_ => Task.CompletedTask)
            .Then(new DelegateStep("the balance check", _ => throw new InvalidOperationException("balance was 0")))
            .RunAsync());
        Assert.Contains("Then #2 (the balance check)", ex.Message);
        Assert.Contains("balance was 0", ex.Message);
    }

    [Fact]
    public async Task scenario_capture_and_with_feed_later_steps()
    {
        var fixture = new UnstartedFixture();
        string? seen = null;
        await fixture.Scenario()
            .With("stake", 5)
            .Given(ctx =>
            {
                ctx.LastResponse = new HttpResult("GET /x", HttpStatusCode.OK, """{ "bet": { "id": "b-1" } }""");
                return Task.CompletedTask;
            })
            .Capture("betId", "bet.id")
            .When(ctx =>
            {
                seen = ctx.Variables.ExpandText("{{betId}}/{{stake}}");
                return Task.CompletedTask;
            })
            .Then(_ => Task.CompletedTask)
            .RunAsync();
        Assert.Equal("b-1/5", seen);
    }

    [Fact]
    public async Task capture_fallback_applies_only_when_the_path_is_missing()
    {
        var fixture = new UnstartedFixture();
        string? seen = null;
        await fixture.Scenario()
            .Given(ctx =>
            {
                ctx.LastResponse = new HttpResult("POST /claim", HttpStatusCode.OK, """{ "key": "k1" }""");
                return Task.CompletedTask;
            })
            .Capture("intervalId", "intervalId", fallback: 0)
            .Capture("key", "key", fallback: "none")
            .When(ctx =>
            {
                seen = ctx.Variables.ExpandJson("""{ "i": {{intervalId}}, "k": "{{key}}" }""")!.ToJsonString();
                return Task.CompletedTask;
            })
            .Then(_ => Task.CompletedTask)
            .RunAsync();
        Assert.Equal("""{"i":0,"k":"k1"}""", seen);
    }

    [Fact]
    public async Task required_capture_names_the_response()
    {
        var fixture = new UnstartedFixture();
        var ex = await Assert.ThrowsAsync<XunitException>(() => fixture.Scenario()
            .Given(ctx =>
            {
                ctx.LastResponse = new HttpResult("POST /claim", HttpStatusCode.OK, "{}");
                return Task.CompletedTask;
            })
            .Capture("id", "id")
            .When(_ => Task.CompletedTask)
            .Then(_ => Task.CompletedTask)
            .RunAsync());
        Assert.Contains("Capture 'id' from POST /claim -> 200 OK", ex.Message);
    }

    [Fact]
    public void contains_within_searches_nested_arrays()
    {
        var context = new ScenarioContext(new UnstartedFixture(), new ScenarioVariables("sbx", "c1"), null, TimeSpan.FromSeconds(1));
        var body = JsonNode.Parse("""
            { "markets": [
                { "id": "m1", "selections": [{ "id": "s1", "dividend": 2 }] },
                { "id": "m2", "selections": [{ "id": "s2", "dividend": 3 }, { "id": "s3" }] } ] }
            """);

        var found = new BodyCheck();
        found.Contains("""{ "id": "s2", "dividend": 3 }""", within: "markets[*].selections");
        found.Contains("""{ "id": "m1" }""", within: "markets");
        found.Contains("""{ "dividend": 2 }""", within: "markets[*].selections[*]");
        found.Verify(body, context, null, "GET /x");

        var missing = new BodyCheck();
        missing.Contains("""{ "id": "s2", "dividend": 4 }""", within: "markets[*].selections");
        var ex = Assert.Throws<JsonMatchException>(() => missing.Verify(body, context, null, "GET /x"));
        Assert.Contains("no element of 3 within markets[*].selections", ex.Message);
        Assert.Contains("$.dividend: expected 4, actual 3", ex.Message);

        var nothing = new BodyCheck();
        nothing.Contains("{}", within: "nope[*]");
        Assert.Contains("Nothing to search within nope[*]", Assert.Throws<JsonMatchException>(() => nothing.Verify(body, context, null, "GET /x")).Message);
    }

    private sealed class UnstartedFixture() : ServiceTestFixture(new ServiceTestOptions());

    private sealed class FakeSerializationInfo : IXunitSerializationInfo
    {
        private readonly Dictionary<string, object?> values = [];

        public void AddValue(string key, object? value, Type? valueType = null) => values[key] = value;

        public object? GetValue(string key) => values[key];
    }
}
