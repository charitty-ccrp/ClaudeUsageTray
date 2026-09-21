namespace ClaudeUsageTray.Models;

public sealed record UsageFetchResult(UsageResponse? Usage, UsageErrorKind Error, string? Message)
{
    public bool IsSuccess => Error == UsageErrorKind.None && Usage is not null;

    public static UsageFetchResult Ok(UsageResponse usage) => new(usage, UsageErrorKind.None, null);
    public static UsageFetchResult Fail(UsageErrorKind kind, string message) => new(null, kind, message);
}
