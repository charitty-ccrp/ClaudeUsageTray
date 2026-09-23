using ClaudeUsageTray.Models;

namespace ClaudeUsageTray.Services;

/// <summary>
/// Decides whether ClaudeUsageTray should open the Claude Code CLI for the user. Pure so it is easy to test; the
/// actual process launch lives in ClaudeCodeLauncher.
/// </summary>
public static class ClaudeCodeRecoveryPolicy
{
    /// <param name="alreadyAttempted">True once a launch has already been tried for the current run of failures
    /// (reset by the caller as soon as a refresh succeeds), so we do not reopen a new window every retry tick.</param>
    public static bool ShouldAutoLaunch(AuthMode mode, UsageErrorKind error, bool autoLaunchEnabled, bool alreadyAttempted, bool processRunning) =>
        mode == AuthMode.ClaudeCodeOAuth
        && error == UsageErrorKind.CredentialUnavailable
        && autoLaunchEnabled
        && !alreadyAttempted
        && !processRunning;
}
