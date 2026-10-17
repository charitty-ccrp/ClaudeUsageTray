using ClaudeUsageTray.Models;

namespace ClaudeUsageTray.Services;

public static class BackoffPolicy
{
    /// <summary>
    /// Delay before the next automatic attempt. Null means "do not retry automatically"
    /// (wait for the user: 401 needs a new credential, NotConfigured needs setup).
    /// </summary>
    public static TimeSpan? NextDelay(UsageErrorKind kind, int consecutiveFailures, TimeSpan normalInterval, TimeSpan? retryAfter = null) => kind switch
    {
        UsageErrorKind.None => normalInterval,
        UsageErrorKind.NotConfigured or UsageErrorKind.Authentication => null,
        UsageErrorKind.Forbidden => Pick(consecutiveFailures, 300, 600, 900),
        UsageErrorKind.RateLimited => AtLeast(Pick(consecutiveFailures, 60, 300, 900), retryAfter),
        _ => Pick(consecutiveFailures, 15, 30, 60, 120),
    };

    /// <summary>Errors that usually fix themselves within a minute or two, so the UI need not shout about them.</summary>
    public static bool IsTransient(UsageErrorKind kind) =>
        kind is UsageErrorKind.Network or UsageErrorKind.Server or UsageErrorKind.RateLimited or UsageErrorKind.InvalidResponse;

    /// <summary>Spreads a delay by +/-20% so retries never fall into lock-step with the server or other clients.</summary>
    public static TimeSpan Jitter(TimeSpan delay) => Jitter(delay, Random.Shared.NextDouble());

    /// <param name="sample">A value in [0, 1]; 0.5 leaves the delay unchanged.</param>
    public static TimeSpan Jitter(TimeSpan delay, double sample) =>
        TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (0.8 + 0.4 * Math.Clamp(sample, 0, 1)));

    private static TimeSpan Pick(int failures, params int[] seconds) =>
        TimeSpan.FromSeconds(seconds[Math.Clamp(failures - 1, 0, seconds.Length - 1)]);

    /// <summary>Honours a longer Retry-After, capped at an hour so a bogus header cannot stall the app.</summary>
    private static TimeSpan AtLeast(TimeSpan value, TimeSpan? requested) =>
        requested is { } r && r > value ? (r > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : r) : value;
}
