namespace ClaudeUsageTray.Models;

/// <summary>What the UI renders. The snapshot is retained on failure (IsStale) so a hiccup never shows 0%.</summary>
public sealed record UsageState(
    UsageSnapshot? Snapshot,
    bool IsStale,
    UsageErrorKind Error,
    string? ErrorMessage,
    DateTimeOffset? LastSuccessfulUpdate,
    DateTimeOffset? NextAttemptAt)
{
    public static UsageState Initial { get; } =
        new(null, false, UsageErrorKind.None, null, null, null);

    /// <summary>
    /// A short-lived, transient failure while a good value is still on screen: the UI keeps showing that value
    /// normally and only says "retrying" in the footer, instead of flagging an error after a single blip.
    /// </summary>
    public bool IsQuietFailure { get; init; }
}
