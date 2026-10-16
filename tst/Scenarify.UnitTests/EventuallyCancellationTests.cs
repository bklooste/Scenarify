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
        var error = await Assert.ThrowsAsync<EventuallyTimeoutException>(() => Eventually.Assert(cts.Token, () =>
        {
            attempts++;
            throw new InvalidOperationException("dependency is down");
        }, TimeSpan.FromSeconds(30), "service healthy"));

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
            cts.Token,
            () => throw new InvalidOperationException("dependency is down"),
            TimeSpan.FromSeconds(30),
            "service healthy"));

        Assert.Contains("CancellationToken was cancelled", error.Message, StringComparison.Ordinal);
        // The point of the change: the reported cause is the assertion's own failure, not the cancellation.
        Assert.Contains("dependency is down", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task a_token_that_is_never_cancelled_behaves_as_before()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        var result = await Eventually.Assert(cts.Token, () =>
        {
            attempts++;
            if (attempts < 3)
                throw new InvalidOperationException("not yet");
            return Task.FromResult(attempts);
        }, TimeSpan.FromSeconds(10), "eventually succeeds");

        Assert.Equal(3, result);
    }

    // The trailing-token form 0.1.11 shipped. The token is a required parameter on these overloads (not optional), so a
    // call that omits it never binds here and xUnit1051 has nothing to flag.

    [Fact]
    public async Task trailing_form_an_already_cancelled_token_fails_immediately_without_evaluating()
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
        Assert.Contains("already cancelled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task trailing_form_cancelling_mid_wait_reports_the_last_real_failure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var error = await Assert.ThrowsAsync<EventuallyTimeoutException>(() => Eventually.Assert(
            () => throw new InvalidOperationException("dependency is down"),
            TimeSpan.FromSeconds(30),
            "service healthy",
            cts.Token));

        Assert.Contains("CancellationToken was cancelled", error.Message, StringComparison.Ordinal);
        Assert.Contains("dependency is down", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task trailing_form_true_honours_a_cancelled_token()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var error = await Assert.ThrowsAsync<EventuallyTimeoutException>(() => Eventually.True(
            () => Task.FromResult(false), TimeSpan.FromSeconds(30), "never true", cts.Token));

        Assert.Contains("already cancelled", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every way of calling the overloads must bind to exactly one of them. This is a compile-time check as much as a
    /// run-time one: an ambiguity here is a build error (CS0121), and the no-token calls sit directly in a test body, so a
    /// warnings-as-errors build also proves xUnit1051 does not fire for them.
    /// </summary>
    [Fact]
    public async Task every_call_shape_binds_to_one_overload()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeout = TimeSpan.FromSeconds(5);

        // No token: Task- and Task<T>-returning lambdas.
        await Eventually.Assert(async () => await Task.CompletedTask);
        await Eventually.Assert(() => Task.CompletedTask, timeout, "x");
        Assert.Equal(1, await Eventually.Assert(async () => await Task.FromResult(1)));
        Assert.Equal(1, await Eventually.Assert(() => Task.FromResult(1), timeout: timeout, because: "x"));
        await Eventually.True(async () => await Task.FromResult(true));
        await Eventually.True(() => Task.FromResult(true), because: "x", timeout: timeout);

        // Token first.
        await Eventually.Assert(ct, async () => await Task.CompletedTask, timeout, "x");
        Assert.Equal(2, await Eventually.Assert(ct, () => Task.FromResult(2)));
        await Eventually.True(ct, () => Task.FromResult(true), timeout, "x");

        // Token last, positional.
        await Eventually.Assert(async () => await Task.CompletedTask, timeout, "x", ct);
        Assert.Equal(3, await Eventually.Assert(async () => await Task.FromResult(3), timeout, "x", ct));
        await Eventually.True(() => Task.FromResult(true), timeout, "x", ct);

        // Token last, with named arguments in position and fully named.
        await Eventually.Assert(() => Task.CompletedTask, timeout: timeout, because: "x", ct: ct);
        Assert.Equal(4, await Eventually.Assert(() => Task.FromResult(4), timeout: timeout, because: "x", ct: ct));
        await Eventually.Assert(assertion: () => Task.CompletedTask, timeout: timeout, because: "x", ct: ct);
        await Eventually.True(condition: () => Task.FromResult(true), timeout: timeout, because: "x", ct: ct);

        // Explicit type argument, and a null timeout.
        Assert.Equal("v", await Eventually.Assert<string>(() => Task.FromResult("v"), null, null, ct));
        Assert.Equal("w", await Eventually.Assert<string>(ct, () => Task.FromResult("w"), null, null));
        await Eventually.Assert(() => Task.CompletedTask, null, null, ct);
        await Eventually.Assert(() => Task.CompletedTask, timeout, $"interpolated {timeout}", ct);
    }
}
