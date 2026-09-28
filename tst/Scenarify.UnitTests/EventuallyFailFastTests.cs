using Xunit.Sdk;

namespace Scenarify.UnitTests;

/// <summary>
/// The consecutive-timeout limit: a broken dependency should cost one timeout, not one per wait.
/// </summary>
/// <remarks>
/// These tests drive <see cref="Eventually"/>'s process-wide counter deliberately, so they set
/// <see cref="Eventually.ConsecutiveTimeoutLimit"/> themselves and reset the count around each case rather than
/// inheriting whatever an earlier test left behind. They are in their own collection so they cannot run beside
/// another test whose timeout would feed the same counter.
/// </remarks>
[Trait("TestType", "UnitTest")]
[CollectionDefinition(nameof(EventuallyFailFastTests), DisableParallelization = true)]
[Collection(nameof(EventuallyFailFastTests))]
public class EventuallyFailFastTests : IDisposable
{
    private readonly int original = Eventually.ConsecutiveTimeoutLimit;
    private readonly TimeSpan originalMinimum = Eventually.MinimumCountedTimeout;

    public EventuallyFailFastTests() =>
        // These cases use millisecond timeouts so they stay fast, which is exactly what the cost gate ignores.
        // Lower the gate so the short waits here do count; a_cheap_timeout_does_not_count restores it to prove the default.
        Eventually.MinimumCountedTimeout = TimeSpan.Zero;

    public void Dispose()
    {
        Eventually.ConsecutiveTimeoutLimit = this.original;
        Eventually.MinimumCountedTimeout = this.originalMinimum;
        Eventually.ResetFailureCount();
        GC.SuppressFinalize(this);
    }

    private static Task<int> AlwaysFails(TimeSpan timeout, string? because = null) =>
        Eventually.Assert<int>(() => throw new InvalidOperationException("dependency is down"), timeout, because);

    [Fact]
    public async Task the_wait_after_the_limit_is_skipped_instead_of_polling()
    {
        Eventually.ConsecutiveTimeoutLimit = 2;
        Eventually.ResetFailureCount();
        var short_ = TimeSpan.FromMilliseconds(120);

        // Two real timeouts: each one polls and reports the dependency failure.
        for (var i = 0; i < 2; i++)
        {
            var real = await Assert.ThrowsAsync<EventuallyTimeoutException>(() => AlwaysFails(short_));
            Assert.Contains("dependency is down", real.Message, StringComparison.Ordinal);
        }

        // The third does not wait at all, and says so rather than looking like its own failure.
        var calls = 0;
        var skipped = await Assert.ThrowsAsync<EventuallyTimeoutException>(() => Eventually.Assert(() =>
        {
            calls++;
            throw new InvalidOperationException("should never run");
        }, TimeSpan.FromSeconds(30), "a later wait"));

        Assert.Equal(0, calls);
        Assert.Contains("not evaluated", skipped.Message, StringComparison.Ordinal);
        Assert.Contains("EVENTUALLY_TIMEOUT_LIMIT", skipped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("should never run", skipped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task skipping_is_fast_where_polling_would_have_been_slow()
    {
        Eventually.ConsecutiveTimeoutLimit = 1;
        Eventually.ResetFailureCount();

        await Assert.ThrowsAsync<EventuallyTimeoutException>(() => AlwaysFails(TimeSpan.FromMilliseconds(120)));

        // A 10s budget the caller never spends: the point of the whole feature.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<EventuallyTimeoutException>(() => AlwaysFails(TimeSpan.FromSeconds(10)));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"expected an immediate failure, took {watch.Elapsed}");
    }

    [Fact]
    public async Task a_success_clears_the_count_so_a_merely_slow_suite_never_trips()
    {
        Eventually.ConsecutiveTimeoutLimit = 2;
        Eventually.ResetFailureCount();
        var short_ = TimeSpan.FromMilliseconds(120);

        await Assert.ThrowsAsync<EventuallyTimeoutException>(() => AlwaysFails(short_));
        await Assert.ThrowsAsync<EventuallyTimeoutException>(() => AlwaysFails(short_));

        // Interleaving one pass resets the run of timeouts...
        Eventually.ResetFailureCount();
        var passed = await Eventually.Assert(() => Task.FromResult(1), TimeSpan.FromSeconds(1));
        Assert.Equal(1, passed);

        // ...so the next failure is evaluated normally rather than skipped.
        var evaluated = await Assert.ThrowsAsync<EventuallyTimeoutException>(() => AlwaysFails(short_));
        Assert.Contains("dependency is down", evaluated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task zero_disables_it_entirely()
    {
        Eventually.ConsecutiveTimeoutLimit = 0;
        Eventually.ResetFailureCount();
        var short_ = TimeSpan.FromMilliseconds(120);

        // However many times it times out, every wait is still evaluated.
        for (var i = 0; i < 4; i++)
        {
            var ex = await Assert.ThrowsAsync<EventuallyTimeoutException>(() => AlwaysFails(short_));
            Assert.Contains("dependency is down", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task a_cheap_timeout_does_not_count_towards_the_limit()
    {
        // The default cost gate: a deliberate fast negative test must never trip the limit for whatever runs next,
        // which is the one way this feature could produce a confusing failure in somebody else's suite.
        Eventually.MinimumCountedTimeout = TimeSpan.FromSeconds(5);
        Eventually.ConsecutiveTimeoutLimit = 2;
        Eventually.ResetFailureCount();
        var cheap = TimeSpan.FromMilliseconds(120);

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<EventuallyTimeoutException>(() => AlwaysFails(cheap));

        // Still evaluated, with the real failure, after five cheap timeouts in a row.
        var ex = await Assert.ThrowsAsync<EventuallyTimeoutException>(() => AlwaysFails(cheap));
        Assert.Contains("dependency is down", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not evaluated", ex.Message, StringComparison.Ordinal);
    }
}
