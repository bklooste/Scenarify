using System.Net;
using System.Text;
using System.Text.Json;

using Xunit.Sdk;

namespace Scenarify;

/// <summary>HTTP steps against the service under test.</summary>
public static class Http
{
    /// <summary>A GET request.</summary>
    public static HttpStep Get(string path) => new(HttpMethod.Get, path, null);

    /// <summary>A POST request with an optional JSON body.</summary>
    public static HttpStep Post(string path, JsonSource? body = null) => new(HttpMethod.Post, path, body);

    /// <summary>A PUT request with an optional JSON body.</summary>
    public static HttpStep Put(string path, JsonSource? body = null) => new(HttpMethod.Put, path, body);

    /// <summary>A PATCH request with an optional JSON body.</summary>
    public static HttpStep Patch(string path, JsonSource? body = null) => new(HttpMethod.Patch, path, body);

    /// <summary>A DELETE request.</summary>
    public static HttpStep Delete(string path) => new(HttpMethod.Delete, path, null);

    /// <summary>A request with any method.</summary>
    public static HttpStep Send(HttpMethod method, string path, JsonSource? body = null) => new(method, path, body);
}

/// <summary>
/// One HTTP request, usable as a Given (must succeed unless a status is set), the When, or a Then
/// (optionally <see cref="Eventually"/>). Paths and bodies expand <c>{{tokens}}</c>. The request carries the
/// fixture's scopes and brand and the scenario's <c>{{customerId}}</c> as <c>userId</c> unless overridden.
/// </summary>
public sealed class HttpStep : IGivenStep, IWhenStep, IThenStep
{
    private readonly HttpMethod method;
    private readonly string path;
    private readonly JsonSource? body;
    private readonly Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly BodyCheck check = new();
    private readonly List<HttpStatusCode> statuses = [];
    private readonly List<(string Name, string Path)> captures = [];
    private readonly List<(string Name, string Value)> expectedHeaders = [];
    private readonly List<string> bodyContains = [];
    private bool anyStatus;
    private bool eventually;
    private TimeSpan? timeout;
    private string? scopes;
    private string? userId;
    private string? brand;
    private string? service;

    internal HttpStep(HttpMethod method, string path, JsonSource? body)
    {
        this.method = method;
        this.path = path;
        this.body = body;
    }

    /// <summary>Expect one of these statuses (default: any 2xx).</summary>
    public HttpStep Status(params HttpStatusCode[] expected)
    {
        statuses.AddRange(expected);
        return this;
    }

    /// <summary>
    /// Expect a response header whose value contains <paramref name="valueContains"/> (tokens expand; case-insensitive),
    /// e.g. <c>.HasHeader("Content-Type", "application/json")</c>.
    /// </summary>
    public HttpStep HasHeader(string name, string valueContains)
    {
        expectedHeaders.Add((name, valueContains));
        return this;
    }

    /// <summary>Expect the raw body (any content type: text, HTML, JavaScript) to contain <paramref name="text"/> (tokens expand).</summary>
    public HttpStep BodyContains(string text)
    {
        bodyContains.Add(text);
        return this;
    }

    /// <summary>After the checks pass, capture a response value into <c>{{name}}</c> (any phase, including eventual Thens).</summary>
    public HttpStep Capture(string name, string path)
    {
        captures.Add((name, path));
        return this;
    }

    /// <summary>
    /// Expect an element that subset-matches <paramref name="element"/>: in the body array, or in the arrays the
    /// <paramref name="within"/> path selects (<c>markets[*].selections</c>).
    /// </summary>
    public HttpStep Contains(JsonSource element, string? within = null)
    {
        check.Contains(element, within);
        return this;
    }

    /// <summary>Accept any status (for arrange steps whose outcome does not matter).</summary>
    public HttpStep AnyStatus()
    {
        anyStatus = true;
        return this;
    }

    /// <summary>Expect the body to contain everything in <paramref name="expected"/> (subset match).</summary>
    public HttpStep Matches(JsonSource expected)
    {
        check.Matches(expected);
        return this;
    }

    /// <summary>Expect the body to equal <paramref name="expected"/>; extra properties fail.</summary>
    public HttpStep MatchesExactly(JsonSource expected)
    {
        check.MatchesExactly(expected);
        return this;
    }

    /// <summary>Expect the body to match its stored snapshot.</summary>
    public HttpStep MatchesSnapshot(string? name = null)
    {
        check.MatchesSnapshot(name);
        return this;
    }

    /// <summary>When recording a new snapshot, keep only these paths. Remove once the snapshot is accepted.</summary>
    public HttpStep Only(params string[] paths)
    {
        check.Only(paths);
        return this;
    }

    /// <summary>Remove these paths before comparing.</summary>
    public HttpStep Ignoring(params string[] paths)
    {
        check.Ignoring(paths);
        return this;
    }

    /// <summary>Compare arrays in any order.</summary>
    public HttpStep UnorderedArrays()
    {
        check.UnorderedArrays();
        return this;
    }

    /// <summary>Assert on the body deserialized as <typeparamref name="T"/>.</summary>
    public HttpStep Satisfies<T>(Action<T> assertion, JsonSerializerOptions? options = null)
    {
        check.Satisfies(assertion, options);
        return this;
    }

    /// <summary>Check the body deserialized as <typeparamref name="T"/> with a predicate.</summary>
    public HttpStep Satisfies<T>(Func<T, bool> predicate, JsonSerializerOptions? options = null)
    {
        check.Satisfies(predicate, options);
        return this;
    }

    /// <summary>Retry request and checks until they pass or the timeout expires (in any phase — a waiting Given is a barrier).</summary>
    public HttpStep Eventually(TimeSpan? within = null)
    {
        eventually = true;
        timeout = within;
        return this;
    }

    /// <summary>Send these scopes instead of the fixture default (<c>""</c> for none). Tokens expand.</summary>
    public HttpStep WithScopes(string value)
    {
        scopes = value;
        return this;
    }

    /// <summary>Send this user id instead of <c>{{customerId}}</c> (<c>""</c> for none). Tokens expand.</summary>
    public HttpStep AsUser(string value)
    {
        userId = value;
        return this;
    }

    /// <summary>Send this brand instead of the fixture's (<c>""</c> for none). Tokens expand.</summary>
    public HttpStep WithBrand(string value)
    {
        brand = value;
        return this;
    }

    /// <summary>Send to another service named in <c>ServiceTestOptions.Services</c> instead of the service under test.</summary>
    public HttpStep On(string serviceName)
    {
        service = serviceName;
        return this;
    }

    /// <summary>Add a request header. Tokens expand.</summary>
    public HttpStep WithHeader(string name, string value)
    {
        headers[name] = value;
        return this;
    }

    internal bool HasStatus => statuses.Count > 0 || anyStatus;

    internal bool HasBodyCheck => check.IsSet || expectedHeaders.Count > 0 || bodyContains.Count > 0;

    /// <inheritdoc />
    public Task GivenAsync(ScenarioContext context) => RunAsync(context);

    /// <inheritdoc />
    public Task WhenAsync(ScenarioContext context) => RunAsync(context);

    /// <inheritdoc />
    public Task ThenAsync(ScenarioContext context) => RunAsync(context);

    private async Task RunAsync(ScenarioContext context)
    {
        var name = check.ResolveSnapshotName(context);
        var result = eventually
            ? await Scenarify.Eventually.Assert(async () => Verify(await SendAsync(context), context, name),
                timeout ?? context.Timeout, $"{method} {context.Variables.ExpandText(path)}")
            : Verify(await SendAsync(context), context, name);

        context.LastResponse = result;
        foreach (var (captureName, capturePath) in captures)
        {
            try
            {
                context.Variables.Set(captureName, JsonPaths.SelectSingle(result.Json, capturePath));
            }
            catch (InvalidOperationException ex)
            {
                throw new XunitException($"Capture '{captureName}' from {result.Describe()}: {ex.Message}");
            }
        }
    }

    /// <summary>Sends the request and returns the response without checking it.</summary>
    public async Task<HttpResult> SendAsync(ScenarioContext context)
    {
        var vars = context.Variables;
        var url = vars.ExpandText(path).TrimStart('/');
        var client = context.Fixture.ClientsFor(service).Client(
            vars.ExpandText(scopes ?? context.Fixture.Options.DefaultScopes ?? ""),
            userId is null ? vars.CustomerId : vars.ExpandText(userId),
            brand is null ? vars.Brand : vars.ExpandText(brand));

        using var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, vars.ExpandText(value));

        string? sent = null;
        if (body is not null)
        {
            sent = body.ResolveText(vars);
            request.Content = new StringContent(sent, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Xunit.TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync();
        var target = service is null ? "" : $"[{service}] ";
        var description = sent is null ? $"{target}{method} /{url}" : $"{target}{method} /{url} {Truncate(sent)}";
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            responseHeaders[name] = string.Join(", ", values);
        return new HttpResult(description, response.StatusCode, text) { Headers = responseHeaders };
    }

    private HttpResult Verify(HttpResult result, ScenarioContext context, string? snapshotName)
    {
        var ok = anyStatus || (statuses.Count == 0 ? (int)result.Status is >= 200 and < 300 : statuses.Contains(result.Status));
        if (!ok)
        {
            var expected = statuses.Count == 0 ? "2xx" : string.Join(" or ", statuses.Select(s => $"{(int)s} {s}"));
            throw new XunitException($"{result.Describe()}, expected {expected}.{Environment.NewLine}Body:{Environment.NewLine}{result.Body}");
        }

        foreach (var (name, value) in expectedHeaders)
        {
            var expected = context.Variables.ExpandText(value);
            if (!result.Headers.TryGetValue(name, out var actual) || !actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new XunitException(
                    $"{result.Describe()}: header {name} expected to contain '{expected}', was '{actual ?? "(missing)"}'. " +
                    $"Headers: {string.Join("; ", result.Headers.Select(h => $"{h.Key}: {h.Value}"))}");
            }
        }

        foreach (var text in bodyContains)
        {
            var expected = context.Variables.ExpandText(text);
            if (!result.Body.Contains(expected, StringComparison.Ordinal))
                throw new XunitException($"{result.Describe()}: body does not contain '{expected}'.{Environment.NewLine}Body:{Environment.NewLine}{Truncate(result.Body)}");
        }

        if (check.IsSet)
            check.Verify(result.Json, context, snapshotName, result.Describe());
        return result;
    }

    /// <inheritdoc />
    public override string ToString() => $"{method} {path}";

    private static string Truncate(string text) => text.Length > 300 ? text[..300] + "..." : text;
}
