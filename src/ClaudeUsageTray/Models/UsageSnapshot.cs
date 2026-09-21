namespace ClaudeUsageTray.Models;

/// <summary>One usage window Claude reported, with the labels the UI uses for it.</summary>
public sealed record UsageWindow(string Key, string Label, string ShortLabel, UsageEntry Entry);

public sealed record UsageSnapshot(UsageResponse Usage, DateTimeOffset FetchedAt, string? PlanName = null)
{
    public const string FiveHourKey = "five_hour";
    public const string SevenDayKey = "seven_day";
    public const string SevenDayOpusKey = "seven_day_opus";
    public const string SevenDaySonnetKey = "seven_day_sonnet";

    public UsageEntry FiveHour => Usage.FiveHour;

    /// <summary>Only the windows Claude actually returned, 5-hour first.</summary>
    public IReadOnlyList<UsageWindow> Windows
    {
        get
        {
            var list = new List<UsageWindow>(4) { new(FiveHourKey, "5-HOUR SESSION", "5h", Usage.FiveHour) };
            if (Usage.SevenDay is { } week) list.Add(new(SevenDayKey, "WEEKLY", "wk", week));
            if (Usage.SevenDayOpus is { } opus) list.Add(new(SevenDayOpusKey, "WEEKLY OPUS", "opus", opus));
            if (Usage.SevenDaySonnet is { } sonnet) list.Add(new(SevenDaySonnetKey, "WEEKLY SONNET", "sonnet", sonnet));
            return list;
        }
    }
}
