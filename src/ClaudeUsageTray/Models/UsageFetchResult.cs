namespace ClaudeUsageTray.Models;

public sealed record UsageFetchResult(UsageResponse? Usage, UsageErrorKind Error, string? Message)
{
    public bool IsSuccess => Error == UsageErrorKind.None && Usage is not null;

    /// <summary>Server-requested wait (HTTP 429 Retry-After), when it sent one.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public static UsageFetchResult Ok(UsageResponse usage) => new(usage, UsageErrorKind.None, null);
    public static UsageFetchResult Fail(UsageErrorKind kind, string message, TimeSpan? retryAfter = null) =>
        new(null, kind, message) { RetryAfter = retryAfter };
}
