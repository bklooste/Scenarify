namespace Scenarify.UnitTests;

[Trait("TestType", "UnitTest")]
public sealed class ConfigLayerTests : IDisposable
{
    private sealed class Fixture(ServiceTestOptions options) : ServiceTestFixture(options);

    private readonly string key = "ScenarifyLayer" + Guid.NewGuid().ToString("N");

    private string Read() => new Fixture(new ServiceTestOptions { Config = new Dictionary<string, string?> { [key] = "default" } }).Config[key]!;

    [Fact]
    public void Global_alone_overrides_default()
    {
        Environment.SetEnvironmentVariable("GLOBAL_" + key, "global");
        Assert.Equal("global", Read());
    }

    [Fact]
    public void Unprefixed_beats_global()
    {
        Environment.SetEnvironmentVariable("GLOBAL_" + key, "global");
        Environment.SetEnvironmentVariable(key, "plain");
        Assert.Equal("plain", Read());
    }

    [Fact]
    public void Dotnet_beats_both()
    {
        Environment.SetEnvironmentVariable("GLOBAL_" + key, "global");
        Environment.SetEnvironmentVariable(key, "plain");
        Environment.SetEnvironmentVariable("DOTNET_" + key, "dotnet");
        Assert.Equal("dotnet", Read());
    }

    public void Dispose()
    {
        foreach (var prefix in new[] { "GLOBAL_", "", "DOTNET_" })
            Environment.SetEnvironmentVariable(prefix + key, null);
    }
}
