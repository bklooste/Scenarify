using System.Collections.Concurrent;

namespace Scenarify;

/// <summary>
/// HTTP clients for a service under test that carry the headers Envoy would forward
/// (<c>auth-claim-scopes</c>, <c>brand</c>, <c>userId</c>), plus a fresh <c>Request-Id</c> per request.
/// Clients are cached per header combination and live as long as the fixture.
/// </summary>
/// <param name="defaultHeaders">Headers every client sends (e.g. <c>Authorization: Bearer …</c> for tests through the gateway); not sent by <see cref="Anonymous"/>.</param>
/// <param name="sendIdentityHeaders">
/// False for tests through the gateway (Envoy + plat-auth derive scopes, brand and user from the token): the
/// <c>auth-claim-scopes</c>/<c>brand</c>/<c>userId</c> headers are then never sent.
/// </param>
public sealed class TestClients(Uri baseAddress, string defaultBrand, string? defaultScopes = null,
    IReadOnlyDictionary<string, string>? defaultHeaders = null, bool sendIdentityHeaders = true) : IDisposable
{
    /// <summary>Header carrying the caller's scopes.</summary>
    public const string ScopesHeader = "auth-claim-scopes";

    /// <summary>Header carrying the brand.</summary>
    public const string BrandHeader = "brand";

    /// <summary>Header carrying the customer id.</summary>
    public const string UserIdHeader = "userId";

    /// <summary>Header carrying the per-request idempotency id.</summary>
    public const string RequestIdHeader = "Request-Id";

    private readonly ConcurrentDictionary<(string?, string?, string?, bool), HttpClient> clients = new();

    /// <summary>The service's base address.</summary>
    public Uri BaseAddress { get; } = baseAddress;

    /// <summary>
    /// A client with the given headers. <paramref name="scopes"/> and <paramref name="brand"/> fall back
    /// to the fixture defaults; pass <c>""</c> to send no header. <paramref name="userId"/> is sent only when given.
    /// </summary>
    public HttpClient Client(string? scopes = null, string? userId = null, string? brand = null) =>
        Create(scopes, userId, brand, withDefaultHeaders: true);

    private HttpClient Create(string? scopes, string? userId, string? brand, bool withDefaultHeaders)
    {
        var key = (scopes ?? defaultScopes, userId, brand ?? defaultBrand, withDefaultHeaders);
        return clients.GetOrAdd(key, k =>
        {
            var client = new HttpClient(new RequestIdHandler { InnerHandler = new SocketsHttpHandler() })
            {
                BaseAddress = BaseAddress,
                Timeout = TimeSpan.FromSeconds(60),
            };
            if (sendIdentityHeaders)
            {
                AddIfSet(client, ScopesHeader, k.Item1);
                AddIfSet(client, UserIdHeader, k.Item2);
                AddIfSet(client, BrandHeader, k.Item3);
            }
            if (k.Item4 && defaultHeaders is not null)
            {
                foreach (var (name, value) in defaultHeaders)
                    client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
            }
            return client;
        });
    }

    /// <summary>A client with no auth, brand or user headers.</summary>
    public HttpClient Anonymous() => Create("", null, "", withDefaultHeaders: false);

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var client in clients.Values)
            client.Dispose();
        clients.Clear();
    }

    private static void AddIfSet(HttpClient client, string header, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            client.DefaultRequestHeaders.Add(header, value);
    }

    private sealed class RequestIdHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains(RequestIdHeader))
                request.Headers.Add(RequestIdHeader, Guid.NewGuid().ToString("N"));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
