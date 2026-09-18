using System.Net;

namespace Scenarify;

/// <summary>MockServer steps. Expectations and paths expand <c>{{tokens}}</c>; nothing here resets MockServer.</summary>
public static class Mock
{
    /// <summary>Given: load expectations in MockServer's native JSON (file or inline).</summary>
    public static IGivenStep Expectations(JsonSource expectations) =>
        new DelegateStep($"mock expectations {expectations}",
            ctx => ctx.Fixture.MockServer.LoadExpectationsAsync(expectations, ctx.Variables));

    /// <summary>Given: <paramref name="method"/> <paramref name="path"/> answers <paramref name="status"/> with an optional JSON body.</summary>
    /// <remarks>When <paramref name="requestBody"/> is given, only requests whose JSON body contains those fields match.</remarks>
    /// <remarks><paramref name="requestHeaders"/> (<c>Request-Id={{requestId}}</c>) restricts it to requests carrying those headers.</remarks>
    public static IGivenStep Stub(string method, string path, JsonSource? response = null, HttpStatusCode status = HttpStatusCode.OK,
        JsonSource? requestBody = null, string? requestHeaders = null, string? query = null) =>
        new DelegateStep($"mock {method} {path}",
            ctx => ctx.Fixture.MockServer.StubAsync(method, path, response, status, ctx.Variables, requestBody: requestBody, requestHeaders: requestHeaders, query: query));

    /// <summary>Given: a GET stub.</summary>
    public static IGivenStep Get(string path, JsonSource? response = null, HttpStatusCode status = HttpStatusCode.OK, string? requestHeaders = null,
        string? query = null) =>
        Stub("GET", path, response, status, requestHeaders: requestHeaders, query: query);

    /// <summary>Given: a POST stub.</summary>
    public static IGivenStep Post(string path, JsonSource? response = null, HttpStatusCode status = HttpStatusCode.OK, JsonSource? requestBody = null,
        string? requestHeaders = null) =>
        Stub("POST", path, response, status, requestBody, requestHeaders);

    /// <summary>Given: a PUT stub.</summary>
    public static IGivenStep Put(string path, JsonSource? response = null, HttpStatusCode status = HttpStatusCode.OK, JsonSource? requestBody = null,
        string? requestHeaders = null) =>
        Stub("PUT", path, response, status, requestBody, requestHeaders);

    /// <summary>Given: a DELETE stub.</summary>
    public static IGivenStep Delete(string path, JsonSource? response = null, HttpStatusCode status = HttpStatusCode.OK, string? requestHeaders = null) =>
        Stub("DELETE", path, response, status, requestHeaders: requestHeaders);

    /// <summary>
    /// Then: MockServer eventually received <paramref name="method"/> <paramref name="path"/> with a body that
    /// subset-matches <paramref name="body"/> (when given), exactly <paramref name="times"/> times (when given), with the
    /// query parameters in <paramref name="query"/> (<c>customerId={{customerId}}&amp;brand={{brand}}</c>) and headers in
    /// <paramref name="headers"/> (same format), when given. Values match literally; <c>*</c> is a wildcard. Usable as a Given barrier too.
    /// </summary>
    public static DelegateStep Received(string method, string path, JsonSource? body = null, int? times = null, TimeSpan? timeout = null,
        string? query = null, string? headers = null) =>
        new($"mock received {method} {path}",
            ctx => ctx.Fixture.MockServer.ReceivedEventuallyAsync(method, path, body, times, ctx.Variables, timeout ?? ctx.Timeout, query, headers));

    /// <summary>
    /// Then: MockServer has <b>not</b> received <paramref name="method"/> <paramref name="path"/> (with a body subset-matching
    /// <paramref name="body"/>, when given). Checked once, immediately — put a step before it that proves the service
    /// has finished processing (for example a marker message and its side effect).
    /// </summary>
    public static IThenStep NotReceived(string method, string path, JsonSource? body = null) =>
        new DelegateStep($"mock not received {method} {path}", async ctx =>
        {
            var expanded = ctx.Variables.ExpandText(path);
            var expected = body?.Resolve(ctx.Variables);
            var recorded = await ctx.Fixture.MockServer.RecordedRequestsAsync(method, expanded);
            var matching = expected is null ? recorded : recorded.Where(r => JsonMatch.IsMatch(expected, r.Json)).ToList();
            if (matching.Count > 0)
                throw new Xunit.Sdk.XunitException(
                    $"MockServer received {matching.Count} {method} {expanded} request(s), expected none. First body: {matching[0].Body}");
        });
}

/// <summary>A step made from a delegate; use for one-off arrange or check code inside a scenario.</summary>
public sealed class DelegateStep(string description, Func<ScenarioContext, Task> run) : IGivenStep, IWhenStep, IThenStep
{
    /// <inheritdoc />
    public Task GivenAsync(ScenarioContext context) => run(context);

    /// <inheritdoc />
    public Task WhenAsync(ScenarioContext context) => run(context);

    /// <inheritdoc />
    public Task ThenAsync(ScenarioContext context) => run(context);

    /// <inheritdoc />
    public override string ToString() => description;
}
