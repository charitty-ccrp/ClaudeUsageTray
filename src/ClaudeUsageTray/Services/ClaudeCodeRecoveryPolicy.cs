using ClaudeUsageTray.Models;

namespace ClaudeUsageTray.Services;

/// <summary>
/// Decides whether ClaudeUsageTray should run the Claude Code CLI to renew its login. Pure so it is easy to test; the
/// actual process launch lives in ClaudeCodeLauncher.
/// </summary>
public static class ClaudeCodeRecoveryPolicy
{
    /// <summary>The renewal runs hidden, so a failed one (e.g. offline) may be retried, but not on every retry tick.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(10);

    /// <param name="lastAttempt">When a renewal was last tried during the current run of failures (the caller clears it
    /// as soon as a refresh succeeds); null if none yet.</param>
    public static bool ShouldAutoLaunch(AuthMode mode, UsageErrorKind error, bool autoLaunchEnabled, DateTimeOffset? lastAttempt,
        DateTimeOffset now, bool processRunning) =>
        mode == AuthMode.ClaudeCodeOAuth
        && error == UsageErrorKind.CredentialUnavailable
        && autoLaunchEnabled
        && (lastAttempt is not { } last || now - last >= RetryInterval)
        && !processRunning;
}
