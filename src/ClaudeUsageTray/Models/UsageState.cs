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
}
