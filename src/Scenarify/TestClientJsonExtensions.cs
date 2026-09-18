using System.Text;

namespace Scenarify;

/// <summary>
/// Sends <see cref="JsonSource"/> bodies from hand-written tests (perf, concurrency) that use <c>fixture.Client()</c>
/// directly instead of a scenario. Tokens expand from <paramref name="variables"/> (use <c>fixture.NewVariables()</c>).
/// </summary>
public static class TestClientJsonExtensions
{
    /// <summary>POST <paramref name="body"/> as JSON.</summary>
    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, JsonSource body, ScenarioVariables? variables = null, CancellationToken ct = default) =>
        SendJsonAsync(client, HttpMethod.Post, path, body, variables, ct);

    /// <summary>PUT <paramref name="body"/> as JSON.</summary>
    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string path, JsonSource body, ScenarioVariables? variables = null, CancellationToken ct = default) =>
        SendJsonAsync(client, HttpMethod.Put, path, body, variables, ct);

    /// <summary>Send <paramref name="body"/> as JSON with any method; the path expands tokens too.</summary>
    public static async Task<HttpResponseMessage> SendJsonAsync(this HttpClient client, HttpMethod method, string path, JsonSource body,
        ScenarioVariables? variables = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(body);
        var url = (variables?.ExpandText(path) ?? path).TrimStart('/');
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body.ResolveText(variables), Encoding.UTF8, "application/json"),
        };
        return await client.SendAsync(request, ct);
    }
}
