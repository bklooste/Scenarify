namespace Scenarify;

/// <summary>
/// Decides when the service under test is ready. Set <see cref="ServiceTestOptions.Readiness"/> to replace the
/// default HTTP <c>/health</c> poll — for workers with no HTTP listener, or where "started" means something else.
/// </summary>
public interface IReadinessProbe
{
    /// <summary>One check; return true when ready, false (or throw) to be asked again. The fixture retries until <see cref="ServiceTestOptions.StartupTimeout"/>.</summary>
    Task<bool> IsReadyAsync(ServiceTestFixture fixture, CancellationToken ct);
}

/// <summary>Built-in <see cref="IReadinessProbe"/>s.</summary>
public static class ReadinessProbe
{
    /// <summary>Wraps a delegate as a probe.</summary>
    public static IReadinessProbe From(Func<ServiceTestFixture, CancellationToken, Task<bool>> check) => new DelegateProbe(check);

    /// <summary>Wraps a delegate that does not need the fixture.</summary>
    public static IReadinessProbe From(Func<CancellationToken, Task<bool>> check) =>
        new DelegateProbe((_, ct) => check(ct));

    /// <summary>Ready immediately: no wait at all. Use only when the first scenario step tolerates a service that is still starting.</summary>
    public static IReadinessProbe None { get; } = new DelegateProbe((_, _) => Task.FromResult(true));

    /// <summary>
    /// "The service has started" proven by an observed effect: each attempt runs <paramref name="publish"/> (a probe or
    /// ping message the service handles) and then <paramref name="observe"/>; ready once observe returns true.
    /// Publish must be safe to repeat, since it runs once per attempt until the effect appears.
    /// </summary>
    public static IReadinessProbe PublishAndObserve(
        Func<ServiceTestFixture, CancellationToken, Task> publish,
        Func<ServiceTestFixture, CancellationToken, Task<bool>> observe)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(observe);
        return new DelegateProbe(async (fixture, ct) =>
        {
            await publish(fixture, ct);
            return await observe(fixture, ct);
        });
    }

    private sealed class DelegateProbe(Func<ServiceTestFixture, CancellationToken, Task<bool>> check) : IReadinessProbe
    {
        public Task<bool> IsReadyAsync(ServiceTestFixture fixture, CancellationToken ct) => check(fixture, ct);
    }
}
