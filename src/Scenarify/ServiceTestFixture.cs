using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Extensions.Configuration;

using MockServerClientNet;

using Xunit;

namespace Scenarify;

/// <summary>
/// Something a fixture starts after configuration is built and before the health wait, and disposes
/// with the fixture — a stream host, a Redis connection. Add-on packages contribute features so the
/// core library takes none of their dependencies.
/// </summary>
public interface IFixtureFeature : IAsyncDisposable
{
    /// <summary>Starts the feature. Called once, in the order features are listed.</summary>
    ValueTask StartAsync(ServiceTestFixture fixture, CancellationToken ct);
}

/// <summary>Everything a <see cref="ServiceTestFixture"/> needs to know about the service under test.</summary>
public sealed record ServiceTestOptions
{
    /// <summary>Initialise MockServer: wait for it, reset it once, load <see cref="MockServerFiles"/>.</summary>
    public bool MockServer { get; init; }

    /// <summary>Expectation files loaded before the health wait (some services call dependencies at startup).</summary>
    public IReadOnlyList<string> MockServerFiles { get; init; } = [];

    /// <summary>Scopes sent when a step gives none; scenario steps expand tokens in it (<c>brand-r:{{brand}}</c>).</summary>
    public string? DefaultScopes { get; init; }

    /// <summary>Brand for clients and the <c>{{brand}}</c> token. Config key <c>TestBrand</c> overrides it.</summary>
    public string Brand { get; init; } = "testBrand";

    /// <summary>
    /// Give every scenario its own <c>{{brand}}</c> (<see cref="Brand"/> plus a fresh suffix) for services that key all
    /// state by brand. Leave off for services that validate the brand against configuration.
    /// </summary>
    public bool UniqueBrandPerScenario { get; init; }

    /// <summary>Features started before the health wait, e.g. <c>RedisFeature</c>.</summary>
    public IReadOnlyList<IFixtureFeature> Features { get; init; } = [];

    /// <summary>In-memory configuration defaults, overridden by environment variables and then by <c>DOTNET_</c> ones.</summary>
    public IReadOnlyDictionary<string, string?> Config { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Other services a test calls directly, by name → the config key holding the base URL (e.g.
    /// <c>["commands"] = "CommandsHttpUrl"</c>). Each is health-checked at start; call one with <c>Http.Get(...).On("commands")</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Services { get; init; } = new Dictionary<string, string>();

    /// <summary>Headers every client sends, e.g. <c>["Authorization"] = "Bearer " + token</c> for tests through the gateway.</summary>
    public IReadOnlyDictionary<string, string> DefaultHeaders { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Send the <c>auth-claim-scopes</c>/<c>brand</c>/<c>userId</c> headers Envoy would forward (default). Set false for
    /// E2E through the gateway, where the token decides them.
    /// </summary>
    public bool SendIdentityHeaders { get; init; } = true;

    /// <summary>Path polled until it returns 200.</summary>
    public string HealthPath { get; init; } = "health";

    /// <summary>How long to wait for the service and MockServer to come up.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// The one fixture every service test uses: configuration, base URL, health wait, MockServer (reset once),
/// header-carrying clients, add-on features and once-per-fixture setup. Subclass only to give xUnit a
/// concrete type with a parameterless constructor.
/// </summary>
/// <example>
/// <code>
/// public sealed class Fixture() : ServiceTestFixture(new ServiceTestOptions { MockServer = true, DefaultScopes = "bonus-r" });
///
/// [CollectionDefinition("ContainerCollection")]
/// public class ContainerCollection : ICollectionFixture&lt;Fixture&gt;;
/// </code>
/// </example>
public abstract class ServiceTestFixture : IAsyncLifetime
{
    /// <summary>Config key holding the service base URL.</summary>
    public const string BaseUrlKey = "SvcHttpUrl";

    /// <summary>Config key holding the MockServer base URL.</summary>
    public const string MockServerUrlKey = "MockServerBaseUri";

    /// <summary>Service base URL when <see cref="BaseUrlKey"/> is not configured.</summary>
    public const string DefaultBaseUrl = "http://localhost:53105";

    /// <summary>MockServer base URL when <see cref="MockServerUrlKey"/> is not configured.</summary>
    public const string DefaultMockServerBaseUrl = "http://localhost:1090";

    private readonly ConcurrentDictionary<string, Lazy<Task>> once = new(StringComparer.Ordinal);
    private MockServerClient? mockServer;
    private readonly Dictionary<string, TestClients> otherClients = new(StringComparer.Ordinal);

    /// <summary>Builds configuration and clients; nothing touches the network until <see cref="InitializeAsync"/>.</summary>
    protected ServiceTestFixture(ServiceTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;

        var defaults = new Dictionary<string, string?>
        {
            [BaseUrlKey] = DefaultBaseUrl,
            [MockServerUrlKey] = DefaultMockServerBaseUrl,
            ["TestBrand"] = options.Brand,
        };
        foreach (var (key, value) in options.Config)
            defaults[key] = value;

        Config = new ConfigurationBuilder()
            .AddInMemoryCollection(defaults)
            .AddEnvironmentVariables()
            .AddEnvironmentVariables("DOTNET_")
            .Build();

        BaseUrl = Config[BaseUrlKey]!.TrimEnd('/') + "/";
        Brand = Config["TestBrand"]!;
        Clients = new TestClients(new Uri(BaseUrl), Brand, options.DefaultScopes, options.DefaultHeaders, options.SendIdentityHeaders);
        foreach (var (name, key) in options.Services)
        {
            var url = Config[key] ?? throw new InvalidOperationException($"Service '{name}' needs config key '{key}' (its base URL).");
            otherClients[name] = new TestClients(new Uri(url.TrimEnd('/') + "/"), Brand, options.DefaultScopes, options.DefaultHeaders, options.SendIdentityHeaders);
        }
    }

    /// <summary>The options this fixture was built with.</summary>
    public ServiceTestOptions Options { get; }

    /// <summary>Merged configuration: defaults, environment, then <c>DOTNET_</c> environment.</summary>
    public IConfiguration Config { get; }

    /// <summary>The service base URL, always ending in <c>/</c>.</summary>
    public string BaseUrl { get; }

    /// <summary>The brand used by clients and the <c>{{brand}}</c> token.</summary>
    public string Brand { get; }

    /// <summary>Header-carrying HTTP clients for the service.</summary>
    public TestClients Clients { get; }

    /// <summary>The MockServer client; throws when <see cref="ServiceTestOptions.MockServer"/> is off.</summary>
    public MockServerClient MockServer =>
        mockServer ?? throw new InvalidOperationException("MockServer is not enabled: set ServiceTestOptions.MockServer = true.");

    /// <summary>A client with the given headers; see <see cref="TestClients.Client"/>.</summary>
    public HttpClient Client(string? scopes = null, string? userId = null, string? brand = null) =>
        Clients.Client(scopes, userId, brand);

    /// <summary>Clients for another service named in <see cref="ServiceTestOptions.Services"/>; null for the service under test.</summary>
    public TestClients ClientsFor(string? service) =>
        service is null ? Clients
        : otherClients.TryGetValue(service, out var clients) ? clients
        : throw new InvalidOperationException($"Service '{service}' is not configured: add it to ServiceTestOptions.Services.");

    /// <summary>The feature of type <typeparamref name="T"/>; throws when it was not configured.</summary>
    public T Feature<T>() where T : IFixtureFeature =>
        Options.Features.OfType<T>().FirstOrDefault()
        ?? throw new InvalidOperationException($"{typeof(T).Name} is not configured: add it to ServiceTestOptions.Features.");

    /// <summary>Fresh scenario variables: this fixture's brand (unique when configured), a new customer id and guid.</summary>
    public ScenarioVariables NewVariables() =>
        new(Options.UniqueBrandPerScenario ? $"{Brand}-{Guid.NewGuid():N}" : Brand, Guid.NewGuid().ToString());

    /// <summary>
    /// Runs <paramref name="setup"/> once per fixture no matter how many tests ask, in any order. Every caller
    /// awaits the same run; a failure is cached and rethrown to each of them.
    /// </summary>
    public Task EnsureOnceAsync(string key, Func<Task> setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        return once.GetOrAdd(key, _ => new Lazy<Task>(setup, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>
    /// Runs <paramref name="setup"/> once per fixture and returns its cached result.
    /// </summary>
    public async Task<T> EnsureOnceAsync<T>(string key, Func<Task<T>> setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var task = once.GetOrAdd(key, _ => new Lazy<Task>(() => setup(), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return task is Task<T> typed
            ? await typed
            : throw new InvalidOperationException($"EnsureOnceAsync key '{key}' was first used with a different result type.");
    }

    /// <summary>Writes a line to the test output (console for fixtures).</summary>
    public void Log(string message) => Console.WriteLine($"[{GetType().Name}] {message}");

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var watch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(Options.StartupTimeout);

        if (Options.MockServer)
        {
            var url = new Uri(Config[MockServerUrlKey]!);
            var client = new MockServerClient(url.DnsSafeHost, url.Port);
            Log($"Waiting for MockServer at {url}");
            await client.WaitUntilReadyAsync(Options.StartupTimeout);
            await client.ResetAsync();
            foreach (var file in Options.MockServerFiles)
                await client.LoadExpectationsAsync(Json.File(file), NewVariables(), cts.Token);
            mockServer = client;
        }

        foreach (var feature in Options.Features)
            await feature.StartAsync(this, cts.Token);

        await Task.WhenAll(otherClients.Values.Prepend(Clients).Select(clients => WaitHealthyAsync(clients, cts.Token)));

        Log($"Ready in {watch.Elapsed.TotalSeconds:0.0}s");
    }

    private Task WaitHealthyAsync(TestClients clients, CancellationToken ct)
    {
        Log($"Waiting for service health at {clients.BaseAddress}{Options.HealthPath}");
        return Eventually.Assert(async () =>
        {
            using var response = await clients.Anonymous().GetAsync(Options.HealthPath, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"health returned {(int)response.StatusCode}");
        }, Options.StartupTimeout, $"service at {clients.BaseAddress} healthy");
    }

    /// <inheritdoc />
    /// <remarks>Override to dispose extra resources; call <c>base.DisposeAsync()</c>.</remarks>
    public virtual async ValueTask DisposeAsync()
    {
        foreach (var feature in Options.Features.Reverse())
            await feature.DisposeAsync();
        Clients.Dispose();
        foreach (var clients in otherClients.Values)
            clients.Dispose();
        mockServer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
