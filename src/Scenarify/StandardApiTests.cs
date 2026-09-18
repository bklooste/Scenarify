using System.Net;

using Xunit;

namespace Scenarify;

/// <summary>
/// Standard API checks that every service should pass.
/// Call from a single [Fact] in each service's test class.
/// </summary>
/// <example>
/// [Fact]
/// public Task standard_api_tests() =>
///     StandardApiTests.Run(fixture);
///
/// // With extra endpoints to verify:
/// [Fact]
/// public Task standard_api_tests() =>
///     StandardApiTests.Run(fixture.BaseUrl, "v1/foo?brand=sbx");
/// </example>
public static class StandardApiTests
{
    /// <summary>
    /// Runs the standard checks against the fixture's service and every service named in
    /// <see cref="ServiceTestOptions.Services"/> (e.g. a second replica). Use when those services are the same app.
    /// </summary>
    public static async Task RunAll(ServiceTestFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        await Run(fixture.BaseUrl);
        foreach (var name in fixture.Options.Services.Keys)
            await Run(fixture.ClientsFor(name).BaseAddress.ToString());
    }

    /// <summary>Runs the standard checks against the fixture's service. <paramref name="moreUrls"/> are checked for 200 only.</summary>
    public static Task Run(ServiceTestFixture fixture, params string[]? moreUrls)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return Run(fixture.BaseUrl, moreUrls);
    }

    /// <summary>
    /// Runs the standard checks with the API document at <paramref name="documentPath"/> (e.g. <c>openapi/v1.json</c>),
    /// or health only when it is null — for services with no swagger (Razor apps, workers).
    /// </summary>
    public static Task RunWithDocument(ServiceTestFixture fixture, string? documentPath, params string[]? moreUrls)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return Run(fixture.BaseUrl, documentPath, moreUrls);
    }

    /// <summary>Runs the standard checks against <paramref name="baseUrl"/>: health (HTTP/1 and HTTP/2) and swagger.</summary>
    public static Task Run(string baseUrl, params string[]? moreUrls) => Run(baseUrl, DefaultDocumentPath, moreUrls);

    /// <summary>The API document path plat-swagger reads.</summary>
    public const string DefaultDocumentPath = "swagger/v1/swagger.json";

    private static async Task Run(string baseUrl, string? documentPath, string[]? moreUrls)
    {
        using var http1 = new HttpClient { BaseAddress = new Uri(baseUrl) };
        using var http2 = new HttpClient
        {
            DefaultRequestVersion = new Version(2, 0),
            BaseAddress = new Uri(baseUrl)
        };

        var ct = TestContext.Current.CancellationToken;

        // Health via HTTP/1 and HTTP/2
        Assert.Equal(HttpStatusCode.OK, (await http1.GetAsync("health", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http2.GetAsync("health", ct)).StatusCode);

        // API document served by the service (consumed by plat-swagger)
        if (documentPath is not null)
            Assert.Equal(HttpStatusCode.OK, (await http1.GetAsync(documentPath, ct)).StatusCode);

        // Any extra URLs the caller wants verified as 200 OK
        if (moreUrls != null)
        {
            foreach (var url in moreUrls)
                Assert.Equal(HttpStatusCode.OK, (await http1.GetAsync(url, ct)).StatusCode);
        }
    }
}
