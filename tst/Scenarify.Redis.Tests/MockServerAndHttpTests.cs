using System.Net;
using System.Text.Json.Nodes;

using Xunit.Sdk;

namespace XUnitRedisTests;

[Collection(ContainerCollection.Name)]
[Trait("TestType", "ServiceTest")]
public class MockServerAndHttpTests(Containers containers)
{
    private readonly MockBackedFixture fixture = containers.Fixture;

    [Fact]
    public async Task http_steps_send_fixture_headers_and_match_bodies()
    {
        await fixture.Scenario()
            .Given(Mock.Get("/things/{{customerId}}", """{ "id": "{{customerId}}", "brand": "{{brand}}", "count": 2, "at": "{{now}}" }"""))
            .When(Http.Get("things/{{customerId}}"))
            .ThenBody("""{ "id": "{{customerId}}", "count": 2, "at": "{{any:datetime}}" }""")
            .Capture("count", "count")
            .Then(Http.Get("things/{{customerId}}").Satisfies<JsonObject>(o => Assert.Equal(2, (int)o["count"]!)))
            .Then(async ctx =>
            {
                Assert.Equal("2", ctx.Variables.GetString("count"));
                var recorded = await fixture.MockServer.RecordedRequestsAsync("GET", ctx.Variables.ExpandText("/things/{{customerId}}"));
                var headers = recorded[0].Raw["headers"]!.ToJsonString();
                Assert.Contains(ctx.Variables.CustomerId, headers);
                Assert.Contains("orders-r orders-w:sbx", headers);
                Assert.Contains("\"sbx\"", headers);
                Assert.Contains("Request-Id", headers);
            })
            .RunAsync();
    }

    [Fact]
    public async Task expectation_files_with_tokens_load_in_native_format()
    {
        await fixture.Scenario()
            .Given(Mock.Expectations("""
                [{ "httpRequest": { "method": "GET", "path": "/native/{{customerId}}" },
                   "httpResponse": { "statusCode": 202, "body": { "type": "JSON", "json": { "who": "{{customerId}}" } } } }]
                """))
            .When(Http.Get("native/{{customerId}}"))
            .ThenStatus(HttpStatusCode.Accepted)
            .ThenBodyExactly("""{ "who": "{{customerId}}" }""")
            .RunAsync();
    }

    [Fact]
    public async Task wrong_status_shows_the_body()
    {
        var ex = await Assert.ThrowsAsync<XunitException>(() => fixture.Scenario()
            .Given(Mock.Get("/fail/{{customerId}}", """{ "title": "nope" }""", HttpStatusCode.BadRequest))
            .When(Http.Get("fail/{{customerId}}"))
            .ThenStatus(HttpStatusCode.OK)
            .RunAsync());
        Assert.Contains("When #1", ex.Message);
        Assert.Contains("400 BadRequest, expected 200 OK", ex.Message);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task received_matches_json_body_subset_and_counts()
    {
        await fixture.Scenario()
            .Given(Mock.Post("/deposits/{{customerId}}"))
            .Given(Http.Post("deposits/{{customerId}}", """{ "amount": 99, "currency": "AUD", "ref": "{{guid}}" }"""))
            .When(Http.Post("deposits/{{customerId}}", """{ "amount": 1, "currency": "AUD" }"""))
            .Then(Mock.Received("POST", "/deposits/{{customerId}}", """{ "amount": 99, "ref": "{{guid}}" }""", times: 1))
            .Then(Mock.Received("POST", "/deposits/{{customerId}}", """{ "currency": "AUD" }""", times: 2))
            .RunAsync();
    }

    [Fact]
    public async Task received_matches_query_parameters()
    {
        await fixture.Scenario()
            .Given(Mock.Post("/q/{{customerId}}"))
            .When(Http.Post("q/{{customerId}}?customerId={{customerId}}&brand={{brand}}"))
            .Then(Mock.Received("POST", "/q/{{customerId}}", times: 1, query: "customerId={{customerId}}&brand={{brand}}"))
            .RunAsync();

        var ex = await Assert.ThrowsAsync<XunitException>(() => fixture.Scenario()
            .Given(Mock.Post("/q2/{{customerId}}"))
            .When(Http.Post("q2/{{customerId}}?brand=other"))
            .Then(Mock.Received("POST", "/q2/{{customerId}}", query: "brand={{brand}}", timeout: TimeSpan.FromSeconds(1)))
            .RunAsync());
        Assert.Contains("?brand=sbx", ex.Message);
    }

    [Fact]
    public async Task one_step_takes_several_checks_contains_and_capture()
    {
        await fixture.Scenario()
            .Given(Mock.Get("/list/{{customerId}}", """[{ "id": "a", "on": false }, { "id": "{{customerId}}", "n": 3 }]"""))
            .When(Http.Get("list/{{customerId}}")
                .Contains("""{ "id": "{{customerId}}" }""")
                .Matches("""[{ "id": "a", "on": "{{absent}}", "n": "{{absent}}" }, { "n": 3 }]""")
                .Satisfies<JsonArray>(a => Assert.Equal(2, a.Count))
                .Capture("first", "$[0].id"))
            .Then(Http.Get("list/{{first}}").Status(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed))
            .RunAsync();

        var ex = await Assert.ThrowsAsync<XunitException>(() => fixture.Scenario()
            .Given(Mock.Get("/list2/{{customerId}}", """[{ "id": "a" }]"""))
            .When(Http.Get("list2/{{customerId}}").Contains("""{ "id": "b" }"""))
            .RunAsync());
        Assert.Contains("no element of 1 matched", ex.Message);
        Assert.Contains("$.id: expected \"b\", actual \"a\"", ex.Message);
    }

    [Fact]
    public async Task waiting_given_multi_when_and_eventual_then_capture()
    {
        var scenario = fixture.Scenario();
        var path = scenario.Variables.ExpandText("/ready/{{customerId}}");
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            await fixture.MockServer.StubAsync("GET", path, Json.Inline("""{ "token": "t-1" }"""));
        }, TestContext.Current.CancellationToken);

        await scenario
            .Given(Http.Get(path).Eventually(TimeSpan.FromSeconds(10)))
            .Given(Mock.Post("/a/{{customerId}}"))
            .Given(Mock.Post("/b/{{customerId}}"))
            .When(Http.Post("a/{{customerId}}"), Http.Post("b/{{customerId}}"))
            .Then(Mock.Received("POST", "/b/{{customerId}}", times: 1))
            .Then(Http.Get(path).Eventually().Matches("""{ "token": "{{any:string}}" }""").Capture("token", "token"))
            .Then(ctx =>
            {
                Assert.Equal("t-1", ctx.Variables["token"]);
                return Task.CompletedTask;
            })
            .RunAsync();
    }

    [Fact]
    public async Task received_matches_headers_and_stub_matches_request_body()
    {
        await fixture.Scenario()
            .Given(Mock.Post("/hb/{{customerId}}", """{ "kind": "gold" }""", requestBody: """{ "tier": "gold" }"""))
            .Given(Mock.Post("/hb/{{customerId}}", """{ "kind": "other" }"""))
            .When(Http.Post("hb/{{customerId}}", """{ "tier": "gold", "x": 1 }""").Matches("""{ "kind": "gold" }"""))
            .Then(Http.Post("hb/{{customerId}}", """{ "tier": "silver" }""").Matches("""{ "kind": "other" }"""))
            .Then(Mock.Received("POST", "/hb/{{customerId}}", times: 2, headers: "userId={{customerId}}&brand={{brand}}"))
            .Then(Mock.Received("POST", "/hb/{{customerId}}", times: 0, headers: "userId=someone-else"))
            .RunAsync();
    }

    [Fact]
    public async Task stubs_match_request_headers_return_scalars_and_steps_check_response_headers()
    {
        await fixture.Scenario()
            .With("requestId", Guid.NewGuid().ToString("N"))
            .Given(Mock.Post("/rh/{{customerId}}", "\"{{guid}}\"", requestHeaders: "X-Trace={{requestId}}"))
            .Given(Mock.Post("/rh/{{customerId}}", "42"))
            .When(Http.Post("rh/{{customerId}}").WithHeader("X-Trace", "{{requestId}}")
                .Matches("\"{{guid}}\"")
                .HasHeader("Content-Type", "application/json"))
            .Then(Http.Post("rh/{{customerId}}").Matches("42"))
            .RunAsync();

        var ex = await Assert.ThrowsAsync<XunitException>(() => fixture.Scenario()
            .Given(Mock.Get("/rh2/{{customerId}}", "{}"))
            .When(Http.Get("rh2/{{customerId}}").HasHeader("X-Missing", "x"))
            .RunAsync());
        Assert.Contains("header X-Missing expected to contain 'x', was '(missing)'", ex.Message);
    }

    [Fact]
    public async Task steps_can_call_another_named_service()
    {
        await fixture.Scenario()
            .Given(Mock.Get("/other/items/{{customerId}}", """{ "detail": "Not enough funds in wallet" }"""))
            .When(Http.Get("items/{{customerId}}").On("other").Matches($$"""{ "detail": "{{Json.Contains("enough funds")}}", "amount": "{{Json.Absent}}" }"""))
            .RunAsync();

        Assert.Throws<InvalidOperationException>(() => fixture.ClientsFor("nope"));
    }

    [Fact]
    public async Task standard_api_tests_cover_named_services_and_json_client_helpers_send_bodies()
    {
        await StandardApiTests.RunAll(fixture);

        var vars = fixture.NewVariables();
        await fixture.MockServer.StubAsync("POST", vars.ExpandText("/json/{{customerId}}"), variables: vars);
        using var response = await fixture.Client().PostJsonAsync("json/{{customerId}}", """{ "who": "{{customerId}}" }""", vars, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await fixture.MockServer.ReceivedEventuallyAsync("POST", "/json/{{customerId}}", """{ "who": "{{customerId}}" }""", times: 1, variables: vars);
    }

    [Fact]
    public async Task query_values_match_literally_with_star_wildcard_and_body_contains_checks_text()
    {
        await fixture.Scenario()
            .Given(Mock.Get("/lit/{{customerId}}", "\"brand-match\"", query: "brand={{brand}}"))
            .Given(Mock.Get("/lit/{{customerId}}", "\"other\""))
            .When(Http.Get("lit/{{customerId}}?brand={{brand}}").BodyContains("brand-match"))
            .Then(Http.Post("lit2/{{customerId}}?url=http://x/a.b?c=1").AnyStatus())
            .Then(Mock.Received("POST", "/lit2/{{customerId}}", times: 1, query: "url=http://x/a.b?c=1"))
            .Then(Mock.Received("POST", "/lit2/{{customerId}}", times: 1, query: "url=http://x/*"))
            .Then(Mock.Received("POST", "/lit2/{{customerId}}", times: 0, query: "url=http://x/aXb?c=1"))
            .RunAsync();

        await StandardApiTests.RunWithDocument(fixture, null);
    }

    [Fact]
    public async Task gateway_style_fixture_sends_bearer_and_no_identity_headers()
    {
        var gateway = new GatewayStyleFixture(containers.MockUrl);
        await gateway.InitializeAsync();
        try
        {
            await gateway.Scenario()
                .Given(Mock.Get("/gw/{{customerId}}", "{}", requestHeaders: "Authorization=Bearer e2e-token"))
                .When(Http.Get("gw/{{customerId}}"))
                .Then(Mock.Received("GET", "/gw/{{customerId}}", times: 1, headers: "Authorization=Bearer e2e-token"))
                .Then(async ctx =>
                {
                    var recorded = await ctx.Fixture.MockServer.RecordedRequestsAsync("GET", ctx.Variables.ExpandText("/gw/{{customerId}}"));
                    var headers = recorded[0].Raw["headers"]!.ToJsonString();
                    Assert.DoesNotContain("auth-claim-scopes", headers, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("\"userId\"", headers, StringComparison.OrdinalIgnoreCase);
                })
                .RunAsync();
        }
        finally
        {
            await gateway.DisposeAsync();
        }
    }

    [Fact]
    public async Task when_concurrently_sends_all_steps()
    {
        await fixture.Scenario()
            .Given(Mock.Post("/cc/{{customerId}}"))
            .WhenConcurrently(Http.Post("cc/{{customerId}}"), Http.Post("cc/{{customerId}}"), Http.Post("cc/{{customerId}}"))
            .Then(Mock.Received("POST", "/cc/{{customerId}}", times: 3))
            .RunAsync();
    }

    [Fact]
    public async Task received_failure_shows_the_nearest_request_diff()
    {
        var ex = await Assert.ThrowsAsync<XunitException>(() => fixture.Scenario()
            .Given(Mock.Post("/near/{{customerId}}"))
            .When(Http.Post("near/{{customerId}}", """{ "amount": 5 }"""))
            .Then(Mock.Received("POST", "/near/{{customerId}}", """{ "amount": 6 }""", timeout: TimeSpan.FromSeconds(1)))
            .RunAsync());
        Assert.Contains("$.amount: expected 6, actual 5", ex.Message);
    }

    [Fact]
    public async Task not_received_passes_for_other_bodies_and_fails_for_a_match()
    {
        await fixture.Scenario()
            .Given(Mock.Post("/nr/{{customerId}}"))
            .When(Http.Post("nr/{{customerId}}", """{ "amount": 5 }"""))
            .Then(Mock.Received("POST", "/nr/{{customerId}}"))
            .Then(Mock.NotReceived("POST", "/nr/{{customerId}}", """{ "amount": 6 }"""))
            .Then(Mock.NotReceived("GET", "/nr/{{customerId}}"))
            .RunAsync();

        var ex = await Assert.ThrowsAsync<XunitException>(() => fixture.Scenario()
            .Given(Mock.Post("/nr2/{{customerId}}"))
            .When(Http.Post("nr2/{{customerId}}", """{ "amount": 5 }"""))
            .Then(Mock.Received("POST", "/nr2/{{customerId}}"))
            .Then(Mock.NotReceived("POST", "/nr2/{{customerId}}"))
            .RunAsync());
        Assert.Contains("expected none", ex.Message);
    }

    [Fact]
    public async Task clear_path_only_clears_that_path()
    {
        var vars = fixture.NewVariables();
        var mock = fixture.MockServer;
        await mock.StubAsync("GET", vars.ExpandText("/keep/{{customerId}}"));
        await mock.StubAsync("GET", vars.ExpandText("/drop/{{customerId}}"));

        await mock.ClearPathAsync(vars.ExpandText("/drop/{{customerId}}"));

        var client = fixture.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(vars.ExpandText("keep/{{customerId}}"), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(vars.ExpandText("drop/{{customerId}}"), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task eventually_get_retries_until_the_stub_appears()
    {
        var scenario = fixture.Scenario();
        var path = scenario.Variables.ExpandText("/later/{{customerId}}");
        _ = Task.Run(async () =>
        {
            await Task.Delay(700);
            await fixture.MockServer.StubAsync("GET", path, Json.Inline("""{ "ready": true }"""));
        }, TestContext.Current.CancellationToken);

        await scenario
            .When(_ => Task.CompletedTask)
            .Then(Http.Get(path).Eventually(TimeSpan.FromSeconds(10)).Matches("""{ "ready": true }"""))
            .RunAsync();
    }

    [Fact]
    public async Task snapshot_round_trip_fail_accept_pass()
    {
        var name = $"MockServerAndHttpTests/{Guid.NewGuid():N}";

        Scenario Build() => fixture.Scenario()
            .With("orderId", Guid.NewGuid())
            .Given(Mock.Get("/orders/{{orderId}}",
                """{ "orderId": "{{orderId}}", "customerId": "{{customerId}}", "status": "Placed", "server": "{{guid}}", "noise": 1 }"""))
            .When(Http.Get("orders/{{orderId}}"))
            .ThenStatus(HttpStatusCode.OK)
            .Then(Http.Get("orders/{{orderId}}").MatchesSnapshot(name).Only("orderId", "customerId", "status"));

        var first = await Assert.ThrowsAsync<XunitException>(() => Build().RunAsync());
        Assert.Contains("No snapshot", first.Message);

        var received = Path.Combine(JsonSnapshot.ReceivedDirectory, name + ".received.json");
        var recorded = JsonNode.Parse(File.ReadAllText(received))!;
        Assert.Equal("""{"orderId":"{{orderId}}","customerId":"{{customerId}}","status":"Placed"}""", recorded["value"]!.ToJsonString());

        var target = Path.Combine(JsonSnapshot.Directory, name + ".snapshot.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(received, target, overwrite: true);

        await Build().RunAsync();
    }

    [Fact]
    public async Task case_record_runs_post_then_mock_received()
    {
        await fixture.RunAsync(new PostThenMockReceived(
            "echo", "echo/{{customerId}}", """{ "customerId": "{{customerId}}" }""", "POST", "/echo/{{customerId}}",
            Expected: """{ "customerId": "{{customerId}}" }""",
            Mocks: ["""[{ "httpRequest": { "method": "POST", "path": "/echo/{{customerId}}" }, "httpResponse": { "statusCode": 200 } }]"""]));
    }

    [Fact]
    public async Task ensure_once_is_shared_across_tests()
    {
        var first = await fixture.EnsureOnceAsync("shared-seed", () => Task.FromResult(Guid.NewGuid()));
        var second = await fixture.EnsureOnceAsync("shared-seed", () => Task.FromResult(Guid.NewGuid()));
        Assert.Equal(first, second);
    }
}
