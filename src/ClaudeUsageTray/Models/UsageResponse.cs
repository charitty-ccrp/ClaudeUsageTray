namespace ClaudeUsageTray.Models;

/// <summary>Parsed usage payload. Only FiveHour is required; weekly windows are kept but not shown in the MVP UI.</summary>
public sealed record UsageResponse(
    UsageEntry FiveHour,
    UsageEntry? SevenDay,
    UsageEntry? SevenDayOpus,
    UsageEntry? SevenDaySonnet);
