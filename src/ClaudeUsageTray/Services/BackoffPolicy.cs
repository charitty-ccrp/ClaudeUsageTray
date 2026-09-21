using ClaudeUsageTray.Models;

namespace ClaudeUsageTray.Services;

public static class BackoffPolicy
{
    /// <summary>
    /// Delay before the next automatic attempt. Null means "do not retry automatically"
    /// (wait for the user: 401 needs a new credential, NotConfigured needs setup).
    /// </summary>
    public static TimeSpan? NextDelay(UsageErrorKind kind, int consecutiveFailures, TimeSpan normalInterval) => kind switch
    {
        UsageErrorKind.None => normalInterval,
        UsageErrorKind.NotConfigured or UsageErrorKind.Authentication => null,
        UsageErrorKind.Forbidden => Pick(consecutiveFailures, 300, 600, 900),
        _ => Pick(consecutiveFailures, 15, 30, 60, 120),
    };

    private static TimeSpan Pick(int failures, params int[] seconds) =>
        TimeSpan.FromSeconds(seconds[Math.Clamp(failures - 1, 0, seconds.Length - 1)]);
}
