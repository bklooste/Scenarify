using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using MockServerClientNet;
using MockServerClientNet.Model;
using MockServerClientNet.Model.Body;

using Xunit.Sdk;

using static MockServerClientNet.Model.Body.Matchers;

namespace Scenarify;

/// <summary>A request MockServer recorded, with its body parsed when it is JSON.</summary>
public sealed record RecordedRequest(string Method, string Path, string? Body, JsonNode? Json, JsonObject Raw);

/// <summary>
/// Extension methods for MockServerClient providing common expectation setup helpers.
/// </summary>
/// <remarks>
/// Scenario tests never call <c>ResetAsync()</c> per test: the fixture resets once at start and tests
/// isolate themselves with unique ids in paths and bodies. Use <see cref="ClearPathAsync"/> when a test
/// genuinely needs a clean slate for its own path.
/// </remarks>
public static class MockServerExtensions
{
    private static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Sets the response status to 200 OK and serializes <paramref name="item"/> as the JSON body.
    /// </summary>
    public static HttpResponse WithOKJsonBody<T>(this HttpResponse mockResponse, T item, JsonSerializerOptions? options = null) where T : class
    {
        return mockResponse
            .WithStatusCode(HttpStatusCode.OK)
            .WithBody(Contents.Json(JsonSerializer.Serialize(item, options ?? DefaultOptions)));
    }

    /// <summary>
    /// Registers a GET expectation on <paramref name="path"/> that returns a 200 OK response with
    /// <paramref name="item"/> serialized as JSON.
    /// </summary>
    public static void SetupGet<T>(this MockServerClient client, string path, T item, JsonSerializerOptions? options = null) where T : class
    {
        client.When(HttpRequest.Request().WithMethod("GET").WithPath(path))
              .Respond(HttpResponse.Response().WithOKJsonBody(item, options));
    }

    /// <summary>
    /// Registers a GET expectation on <paramref name="path"/> that returns a 404 Not Found response.
    /// </summary>
    public static void SetupGetNotFound(this MockServerClient client, string path)
    {
        client.When(HttpRequest.Request().WithMethod("GET").WithPath(path))
              .Respond(HttpResponse.Response().WithStatusCode(HttpStatusCode.NotFound).WithBody(string.Empty));
    }

    /// <summary>
    /// Registers a POST expectation on <paramref name="path"/> that returns a 200 OK response with
    /// <paramref name="item"/> serialized as JSON. Optionally filters by a substring in the request body.
    /// </summary>
    public static void SetupPost<T>(this MockServerClient client, string path, T item, string? requestBodyFilter = null, JsonSerializerOptions? options = null) where T : class
    {
        var request = requestBodyFilter is null
            ? HttpRequest.Request().WithMethod("POST").WithPath(path)
            : HttpRequest.Request().WithMethod("POST").WithPath(path).WithBody(MatchingSubString(requestBodyFilter));

        client.When(request)
              .Respond(HttpResponse.Response().WithOKJsonBody(item, options));
    }

    /// <summary>
    /// Registers a POST expectation on <paramref name="path"/> that returns a 200 OK response with
    /// an empty body. Optionally filters by a substring in the request body.
    /// </summary>
    public static void SetupPost(this MockServerClient client, string path, string? requestBodyFilter = null)
    {
        var request = requestBodyFilter is null
            ? HttpRequest.Request().WithMethod("POST").WithPath(path)
            : HttpRequest.Request().WithMethod("POST").WithPath(path).WithBody(MatchingSubString(requestBodyFilter));

        client.When(request)
              .Respond(HttpResponse.Response().WithStatusCode(HttpStatusCode.OK).WithBody(string.Empty));
    }

    /// <summary>
    /// Loads expectations written in MockServer's native JSON (a single expectation or an array), with
    /// <c>{{tokens}}</c> expanded from <paramref name="variables"/>. Existing <c>mockserver/*.json</c> files load unchanged.
    /// </summary>
    public static async Task LoadExpectationsAsync(this MockServerClient client, JsonSource expectations, ScenarioVariables? variables = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        var json = expectations.ResolveText(variables);
        using var response = await PutAsync(client, "/mockserver/expectation", json, ct);
        if (response.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException(
                $"MockServer rejected expectations from {expectations.Describe()} ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>
    /// Registers one expectation: <paramref name="method"/> on <paramref name="path"/> answers with
    /// <paramref name="status"/> and an optional JSON body.
    /// </summary>
    /// <remarks><paramref name="requestBody"/> restricts the stub to requests whose JSON body contains those fields (MockServer ONLY_MATCHING_FIELDS).</remarks>
    /// <remarks>
    /// <paramref name="requestHeaders"/> (<c>Request-Id={{requestId}}&amp;brand=x</c>) and <paramref name="query"/>
    /// (<c>brand={{brand}}</c>) restrict it to requests carrying those headers / query parameters (values match literally).
    /// </remarks>
    public static Task StubAsync(this MockServerClient client, string method, string path, JsonSource? responseBody = null, HttpStatusCode status = HttpStatusCode.OK,
        ScenarioVariables? variables = null, CancellationToken ct = default, JsonSource? requestBody = null, string? requestHeaders = null,
        string? query = null)
    {
        var httpResponse = new JsonObject { ["statusCode"] = (int)status };
        if (responseBody is not null)
        {
            httpResponse["headers"] = new JsonObject { ["Content-Type"] = new JsonArray("application/json") };
            var resolved = responseBody.Resolve(variables);
            // MockServer's JSON body type only takes objects and arrays; send scalars (a bare "id" string, a number) as raw text.
            httpResponse["body"] = resolved is JsonObject or JsonArray
                ? new JsonObject { ["type"] = "JSON", ["json"] = resolved }
                : new JsonObject { ["type"] = "STRING", ["string"] = resolved?.ToJsonString() ?? "null" };
        }

        var httpRequest = new JsonObject { ["method"] = method, ["path"] = variables?.ExpandText(path) ?? path };
        if (!string.IsNullOrEmpty(requestHeaders))
            httpRequest["headers"] = ParseQuery(variables?.ExpandText(requestHeaders) ?? requestHeaders);
        if (!string.IsNullOrEmpty(query))
            httpRequest["queryStringParameters"] = ParseQuery(variables?.ExpandText(query) ?? query);
        if (requestBody is not null)
            httpRequest["body"] = new JsonObject { ["type"] = "JSON", ["json"] = requestBody.Resolve(variables), ["matchType"] = "ONLY_MATCHING_FIELDS" };

        var expectation = new JsonObject
        {
            ["httpRequest"] = httpRequest,
            ["httpResponse"] = httpResponse,
        };
        return client.LoadExpectationsAsync(Json.Inline(expectation.ToJsonString()), null, ct);
    }

    /// <summary>Requests MockServer has recorded for <paramref name="method"/> and <paramref name="path"/> (either may be null for any).</summary>
    public static async Task<IReadOnlyList<RecordedRequest>> RecordedRequestsAsync(this MockServerClient client, string? method = null, string? path = null,
        CancellationToken ct = default, string? query = null, string? headers = null)
    {
        var matcher = new JsonObject();
        if (method is not null)
            matcher["method"] = method;
        if (path is not null)
            matcher["path"] = path;
        if (!string.IsNullOrEmpty(query))
            matcher["queryStringParameters"] = ParseQuery(query);
        if (!string.IsNullOrEmpty(headers))
            matcher["headers"] = ParseQuery(headers);

        using var response = await PutAsync(client, "/mockserver/retrieve?type=REQUESTS&format=JSON", matcher.ToJsonString(), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MockServer retrieve failed ({(int)response.StatusCode}): {text}");

        var list = new List<RecordedRequest>();
        if (string.IsNullOrWhiteSpace(text) || JsonNode.Parse(text) is not JsonArray array)
            return list;

        foreach (var item in array.OfType<JsonObject>())
        {
            var body = ReadBody(item["body"]);
            JsonNode? json = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    json = JsonNode.Parse(body);
                }
                catch (JsonException)
                {
                }
            }

            list.Add(new RecordedRequest(
                item["method"]?.GetValue<string>() ?? "",
                item["path"]?.GetValue<string>() ?? "",
                body,
                json,
                item));
        }

        return list;
    }

    /// <summary>
    /// Waits until MockServer has received <paramref name="method"/> <paramref name="path"/>, optionally with a
    /// JSON body that subset-matches <paramref name="body"/>, and (when given) exactly <paramref name="times"/> such requests.
    /// <paramref name="query"/> (<c>a=1&amp;b=2</c>) requires those query parameters and <paramref name="headers"/>
    /// (same format, <c>userId={{customerId}}</c>) those headers.
    /// </summary>
    public static Task<IReadOnlyList<RecordedRequest>> ReceivedEventuallyAsync(this MockServerClient client, string method, string path,
        JsonSource? body = null, int? times = null, ScenarioVariables? variables = null, TimeSpan? timeout = null, string? query = null,
        string? headers = null)
    {
        var expandedPath = variables?.ExpandText(path) ?? path;
        var expandedQuery = query is null ? null : variables?.ExpandText(query) ?? query;
        var expandedHeaders = headers is null ? null : variables?.ExpandText(headers) ?? headers;
        var expected = body?.Resolve(variables);
        return Eventually.Assert(async () =>
        {
            var recorded = await client.RecordedRequestsAsync(method, expandedPath, query: expandedQuery, headers: expandedHeaders);
            var matching = expected is null
                ? recorded
                : recorded.Where(r => JsonMatch.IsMatch(expected, r.Json)).ToList();

            var ok = times is null ? matching.Count > 0 : matching.Count == times;
            if (ok)
                return (IReadOnlyList<RecordedRequest>)matching;

            var sb = new StringBuilder();
            sb.Append($"MockServer received {matching.Count} matching {method} {expandedPath}{(expandedQuery is null ? "" : "?" + expandedQuery)}{(expandedHeaders is null ? "" : $" [headers {expandedHeaders}]")} request(s), expected {(times is null ? "at least 1" : times.ToString())}.");
            if (expected is not null)
            {
                sb.AppendLine().Append("Expected body (subset): ").Append(expected.ToJsonString());
                foreach (var r in recorded.Take(3))
                {
                    sb.AppendLine().AppendLine("Nearest recorded request:");
                    sb.Append(JsonMatch.FormatFailure(JsonMatch.Compare(expected, r.Json), r.Json));
                }
            }
            if (recorded.Count == 0)
                sb.AppendLine().Append("No request to that method and path was recorded at all.");
            throw new XunitException(sb.ToString());
        }, timeout, $"{method} {expandedPath} received");
    }

    /// <summary>Clears expectations and recorded requests for one path only; safe under parallel tests.</summary>
    public static async Task ClearPathAsync(this MockServerClient client, string path, string? method = null, CancellationToken ct = default)
    {
        var matcher = new JsonObject { ["path"] = path };
        if (method is not null)
            matcher["method"] = method;
        using var response = await PutAsync(client, "/mockserver/clear?type=ALL", matcher.ToJsonString(), ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Waits until MockServer answers its status endpoint.</summary>
    public static Task WaitUntilReadyAsync(this MockServerClient client, TimeSpan? timeout = null) =>
        Eventually.Assert(async () =>
        {
            using var response = await PutAsync(client, "/mockserver/status", "", CancellationToken.None);
            response.EnsureSuccessStatusCode();
        }, timeout ?? TimeSpan.FromSeconds(120), $"MockServer at {client.ServerAddress("")} ready");

    private static async Task<HttpResponseMessage> PutAsync(MockServerClient client, string path, string json, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await Http.PutAsync(client.ServerAddress(path), content, ct);
    }

    private static JsonObject ParseQuery(string query)
    {
        var result = new JsonObject();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            // MockServer treats parameter and header values as regexes; match literally, with * as the only wildcard.
            var value = LiteralRegex(parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            if (result[key] is JsonArray values)
                values.Add(value);
            else
                result[key] = new JsonArray(value);
        }
        return result;
    }

    private static string LiteralRegex(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '*')
            {
                sb.Append(".*");
                continue;
            }
            if ("\\^$.|?+()[]{}".Contains(c))
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? ReadBody(JsonNode? body)
    {
        switch (body)
        {
            case null:
                return null;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                return v.GetValue<string>();
            case JsonObject o when o["type"] is JsonValue t && t.GetValueKind() == JsonValueKind.String:
                return t.GetValue<string>() switch
                {
                    "JSON" => o["json"] is JsonValue jv && jv.GetValueKind() == JsonValueKind.String ? jv.GetValue<string>() : o["json"]?.ToJsonString(),
                    "STRING" => o["string"]?.GetValue<string>(),
                    _ when o["rawBytes"] is JsonValue raw => Encoding.UTF8.GetString(Convert.FromBase64String(raw.GetValue<string>())),
                    _ when o["base64Bytes"] is JsonValue b64 => Encoding.UTF8.GetString(Convert.FromBase64String(b64.GetValue<string>())),
                    _ => o.ToJsonString(),
                };
            default:
                return body.ToJsonString();
        }
    }
}
