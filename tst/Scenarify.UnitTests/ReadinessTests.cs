namespace Scenarify.UnitTests;

public class ReadinessTests
{
    private sealed class Fixture(ServiceTestOptions options) : ServiceTestFixture(options);

    [Fact]
    public async Task Worker_without_probe_starts_without_http()
    {
        await using var fixture = new Fixture(new ServiceTestOptions { ServesHttp = false, StartupTimeout = TimeSpan.FromSeconds(5) });
        await fixture.InitializeAsync();
        await StandardApiTests.Run(fixture); // no-op, must not hit /health
        await StandardApiTests.RunAll(fixture);
    }

    [Fact]
    public async Task Custom_probe_replaces_health_poll_and_is_retried()
    {
        var calls = 0;
        var probe = ReadinessProbe.From(_ => Task.FromResult(++calls >= 3));
        await using var fixture = new Fixture(new ServiceTestOptions { Readiness = probe, StartupTimeout = TimeSpan.FromSeconds(10) });
        await fixture.InitializeAsync();
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task PublishAndObserve_publishes_each_attempt_until_observed()
    {
        var published = 0;
        var probe = ReadinessProbe.PublishAndObserve(
            (_, _) => { published++; return Task.CompletedTask; },
            (_, _) => Task.FromResult(published >= 2));
        await using var fixture = new Fixture(new ServiceTestOptions { Readiness = probe, ServesHttp = false, StartupTimeout = TimeSpan.FromSeconds(10) });
        await fixture.InitializeAsync();
        Assert.Equal(2, published);
    }

    [Fact]
    public async Task None_is_ready_immediately()
    {
        await using var fixture = new Fixture(new ServiceTestOptions { Readiness = ReadinessProbe.None });
        await fixture.InitializeAsync();
    }

    [Fact]
    public async Task Probe_that_never_succeeds_times_out()
    {
        await using var fixture = new Fixture(new ServiceTestOptions
        {
            Readiness = ReadinessProbe.From(_ => Task.FromResult(false)),
            StartupTimeout = TimeSpan.FromMilliseconds(500),
        });
        await Assert.ThrowsAsync<EventuallyTimeoutException>(async () => await fixture.InitializeAsync());
    }
}
