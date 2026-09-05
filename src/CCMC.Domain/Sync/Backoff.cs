namespace CCMC.Domain.Sync;

/// <summary>
/// Deterministic exponential backoff, a pure function of attempt count - no
/// jitter, same reasoning as the legacy gateway design documented in
/// context.md: at this system's actual scale (one app instance, a handful of
/// receptions per hour per centre), a thundering herd cannot occur, so jitter
/// would only make behaviour harder to test for no real benefit.
/// </summary>
public static class Backoff
{
    public static TimeSpan Compute(int attemptCount, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
    {
        if (attemptCount < 1) throw new ArgumentOutOfRangeException(nameof(attemptCount), "Attempt count must be >= 1.");

        var effectiveBase = baseDelay ?? TimeSpan.FromSeconds(1);
        var effectiveMax = maxDelay ?? TimeSpan.FromMinutes(5);

        var multiplier = Math.Pow(2, attemptCount - 1);
        var delayMs = effectiveBase.TotalMilliseconds * multiplier;

        if (double.IsInfinity(delayMs) || delayMs > effectiveMax.TotalMilliseconds)
        {
            return effectiveMax;
        }

        return TimeSpan.FromMilliseconds(delayMs);
    }
}
