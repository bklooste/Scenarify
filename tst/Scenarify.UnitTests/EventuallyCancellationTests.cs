using System.Diagnostics;

namespace Scenarify.UnitTests;

/// <summary>
/// A caller's token bounds the retrying. Retrying past it cannot change the answer: every attempt is cancelled before
/// it reaches the network, so polling on only hides whoever spent the budget behind a wall of "A task was canceled".
/// </summary>
[Trait("TestType", "UnitTest")]
public class EventuallyCancellationTests
{
    [Fact]
    public async Task an_already_cancelled_token_fails_immediately_without_evaluating()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var attempts = 0;

        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<EventuallyTimeoutException>(() => Eventually.Assert(() =>
        {
            attempts++;
            throw new InvalidOperationException("dependency is down");
        }, TimeSpan.FromSeconds(30), "service healthy", cts.Token));

        Assert.Equal(0, attempts);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Took {watch.Elapsed}, so it polled rather than giving up.");
        Assert.Contains("was not evaluated", error.Message, StringComparison.Ordinal);
        Assert.Contains("already cancelled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task cancelling_mid_wait_reports_the_last_real_failure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var error = await Assert.ThrowsAsync<EventuallyTimeoutException>(() => Eventually.Assert(
            () => throw new InvalidOperationException("dependency is down"),
            TimeSpan.FromSeconds(30),
            "service healthy",
            cts.Token));

        Assert.Contains("CancellationToken was cancelled", error.Message, StringComparison.Ordinal);
        // The point of the change: the reported cause is the assertion's own failure, not the cancellation.
        Assert.Contains("dependency is down", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task a_token_that_is_never_cancelled_behaves_as_before()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        var result = await Eventually.Assert(() =>
        {
            attempts++;
            if (attempts < 3)
                throw new InvalidOperationException("not yet");
            return Task.FromResult(attempts);
        }, TimeSpan.FromSeconds(10), "eventually succeeds", cts.Token);

        Assert.Equal(3, result);
    }
}
