using System.Diagnostics;
using System.Globalization;

using Xunit;
using Xunit.Sdk;

namespace Scenarify;

/// <summary>Thrown when <see cref="Eventually"/> gives up; the message and inner exception are the last failure.</summary>
public sealed class EventuallyTimeoutException(string message, Exception? inner) : XunitException(message, inner)
{
}

/// <summary>
/// Retries an assertion until it stops throwing. Replaces fixed <c>Task.Delay</c> waits and the old
/// poll-until-true retry loops, and reports the last real failure instead of "returned false".
/// </summary>
public static class Eventually
{
    private static int consecutiveTimeouts;

    /// <summary>
    /// The default timeout: <c>EVENTUALLY_TIMEOUT_SECONDS</c> when set, otherwise 30 seconds.
    /// </summary>
    public static TimeSpan DefaultTimeout { get; set; } = ReadDefaultTimeout();

    /// <summary>
    /// How many timeouts in a row mean "the environment is broken, stop waiting": once this many
    /// <see cref="Assert{T}"/> calls have each waited out their full timeout back to back, the calls after them
    /// fail immediately instead of polling. Any success resets the count. <c>EVENTUALLY_TIMEOUT_LIMIT</c> when set,
    /// otherwise 3. Set to 0 to poll every time regardless.
    /// </summary>
    /// <remarks>
    /// A timeout is not a cheap failure — it costs the whole timeout, every time. One is a failing assertion; three
    /// in a row is almost always a dependency that is down, and the tests after it will each pay the full timeout to
    /// learn the same thing. On a suite with dozens of waits that turns a broken environment into tens of minutes of
    /// CI for no extra information. The count is process-wide, so it works across a whole test run rather than one
    /// class, and a single success clears it — a suite that is merely slow never trips it.
    /// </remarks>
    public static int ConsecutiveTimeoutLimit { get; set; } = ReadTimeoutLimit();

    /// <summary>
    /// The shortest timeout that counts towards <see cref="ConsecutiveTimeoutLimit"/>. Default 5 seconds.
    /// </summary>
    /// <remarks>
    /// The limit exists to stop a broken dependency burning wall-clock, so only a wait that actually cost some is
    /// worth counting. A test that deliberately drives an assertion to time out gives it a tiny budget
    /// (<c>TimeSpan.FromMilliseconds(100)</c> and the like) because it wants the failure quickly; counting those would
    /// let a handful of ordinary negative tests trip the limit for whatever ran next, which is the one way this feature
    /// could turn into a confusing false failure. A real "the service never came up" wait uses seconds, so the two are
    /// easy to tell apart by cost.
    /// </remarks>
    public static TimeSpan MinimumCountedTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Clears the consecutive-timeout count, so the next <see cref="Assert{T}"/> polls normally.</summary>
    /// <remarks>
    /// Call this when a test deliberately drives an assertion to time out, so that expected timeout does not count
    /// towards tripping the limit for unrelated tests that run after it.
    /// </remarks>
    public static void ResetFailureCount() => Interlocked.Exchange(ref consecutiveTimeouts, 0);

    /// <summary>Retries <paramref name="assertion"/> until it completes without throwing.</summary>
    public static async Task Assert(Func<Task> assertion, TimeSpan? timeout = null, string? because = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        await Assert<object?>(async () =>
        {
            await assertion();
            return null;
        }, timeout, because, ct);
    }

    /// <summary>Retries <paramref name="assertion"/> until it returns without throwing, then returns its result.</summary>
    /// <param name="ct">
    /// The caller's token, when the assertion is given one. Retrying cannot outlive it, so a token that is already
    /// cancelled fails immediately rather than spending the whole timeout re-learning that.
    /// </param>
    public static async Task<T> Assert<T>(Func<Task<T>> assertion, TimeSpan? timeout = null, string? because = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var limit = timeout ?? DefaultTimeout;
        var testCt = TestContext.Current.CancellationToken;
        var giveUpLimit = ConsecutiveTimeoutLimit;

        // An already-cancelled caller token cannot produce a different answer on attempt 244 than on attempt 1: every
        // call the assertion makes is cancelled before it reaches the network, so polling on reports "still failing
        // after 119.6s: A task was canceled" and hides the real problem, which is whoever spent the budget first.
        if (ct.IsCancellationRequested)
        {
            var what = because is null ? "This condition" : $"'{because}'";
            throw new EventuallyTimeoutException(
                $"{what} was not evaluated: the caller's CancellationToken was already cancelled before the first "
                + "attempt, so every attempt would fail the same way. Whatever consumed that token's budget is the "
                + "real failure.",
                new OperationCanceledException(ct));
        }

        // Nothing here has been evaluated: say so plainly, so this is not read as the assertion's own failure.
        if (giveUpLimit > 0 && Volatile.Read(ref consecutiveTimeouts) >= giveUpLimit)
        {
            var skipped = because is null ? "This condition was" : $"'{because}' was";
            var seconds = limit.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            throw new EventuallyTimeoutException(
                $"{skipped} not evaluated: the previous {giveUpLimit} waits each timed out, so the environment is being "
                + $"treated as broken rather than slow, and this wait was skipped instead of spending another {seconds}s "
                + "learning the same thing. Fix the earlier failure — that one is real. "
                + $"({nameof(Eventually)}.{nameof(ConsecutiveTimeoutLimit)} or EVENTUALLY_TIMEOUT_LIMIT tunes this; 0 disables it.)",
                null);
        }

        var watch = Stopwatch.StartNew();
        var attempts = 0;
        var delay = TimeSpan.FromMilliseconds(50);

        while (true)
        {
            attempts++;
            try
            {
                var result = await assertion();
                Interlocked.Exchange(ref consecutiveTimeouts, 0);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !(testCt.IsCancellationRequested || ct.IsCancellationRequested))
            {
                // Cancelled part-way through: stop here and report the last real failure, rather than polling on
                // against a dead token until the timeout and reporting "A task was canceled" instead.
                if (ct.IsCancellationRequested)
                {
                    var cancelled = because is null ? "Condition" : $"'{because}'";
                    throw new EventuallyTimeoutException(
                        string.Create(CultureInfo.InvariantCulture,
                            $"{cancelled} was still failing after {attempts} attempts over {watch.Elapsed.TotalSeconds:0.0}s when the caller's CancellationToken was cancelled. Last failure:{Environment.NewLine}{ex.Message}"),
                        ex);
                }

                if (watch.Elapsed + delay >= limit)
                {
                    // Only an expensive wait counts: see MinimumCountedTimeout.
                    if (limit >= MinimumCountedTimeout)
                        Interlocked.Increment(ref consecutiveTimeouts);

                    var what = because is null ? "Condition" : $"'{because}'";
                    throw new EventuallyTimeoutException(
                        string.Create(CultureInfo.InvariantCulture,
                            $"{what} still failing after {attempts} attempts over {watch.Elapsed.TotalSeconds:0.0}s. Last failure:{Environment.NewLine}{ex.Message}"),
                        ex);
                }
            }

            await Task.Delay(delay, testCt);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 500));
        }
    }

    /// <summary>Retries until <paramref name="condition"/> returns true.</summary>
    public static Task True(Func<Task<bool>> condition, TimeSpan? timeout = null, string? because = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return Assert(async () =>
        {
            if (!await condition())
                throw new XunitException($"{because ?? "Condition"} returned false");
        }, timeout, because, ct);
    }

    private static int ReadTimeoutLimit()
    {
        var raw = Environment.GetEnvironmentVariable("EVENTUALLY_TIMEOUT_LIMIT");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit >= 0
            ? limit
            : 3;
    }

    private static TimeSpan ReadDefaultTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("EVENTUALLY_TIMEOUT_SECONDS");
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(30);
    }
}
