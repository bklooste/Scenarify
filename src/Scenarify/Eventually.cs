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
    /// <summary>
    /// The default timeout: <c>EVENTUALLY_TIMEOUT_SECONDS</c> when set, otherwise 30 seconds.
    /// </summary>
    public static TimeSpan DefaultTimeout { get; set; } = ReadDefaultTimeout();

    /// <summary>Retries <paramref name="assertion"/> until it completes without throwing.</summary>
    public static async Task Assert(Func<Task> assertion, TimeSpan? timeout = null, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        await Assert<object?>(async () =>
        {
            await assertion();
            return null;
        }, timeout, because);
    }

    /// <summary>Retries <paramref name="assertion"/> until it returns without throwing, then returns its result.</summary>
    public static async Task<T> Assert<T>(Func<Task<T>> assertion, TimeSpan? timeout = null, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var limit = timeout ?? DefaultTimeout;
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var attempts = 0;
        var delay = TimeSpan.FromMilliseconds(50);

        while (true)
        {
            attempts++;
            try
            {
                return await assertion();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (watch.Elapsed + delay >= limit)
                {
                    var what = because is null ? "Condition" : $"'{because}'";
                    throw new EventuallyTimeoutException(
                        string.Create(CultureInfo.InvariantCulture,
                            $"{what} still failing after {attempts} attempts over {watch.Elapsed.TotalSeconds:0.0}s. Last failure:{Environment.NewLine}{ex.Message}"),
                        ex);
                }
            }

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 500));
        }
    }

    /// <summary>Retries until <paramref name="condition"/> returns true.</summary>
    public static Task True(Func<Task<bool>> condition, TimeSpan? timeout = null, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return Assert(async () =>
        {
            if (!await condition())
                throw new XunitException($"{because ?? "Condition"} returned false");
        }, timeout, because);
    }

    private static TimeSpan ReadDefaultTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("EVENTUALLY_TIMEOUT_SECONDS");
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(30);
    }
}
