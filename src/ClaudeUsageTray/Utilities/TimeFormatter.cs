using System.Globalization;

namespace ClaudeUsageTray.Utilities;

public static class TimeFormatter
{
    /// <summary>"02h 17m". Partial minutes round up so 00h 00m only appears at or after the reset.</summary>
    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "00h 00m";
        var totalMinutes = (int)Math.Ceiling(remaining.TotalMinutes);
        return $"{totalMinutes / 60:00}h {totalMinutes % 60:00}m";
    }

    /// <summary>"2d 05h" for long windows (weekly); falls back to the hours/minutes format under a day.</summary>
    public static string FormatRemainingLong(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromDays(1)) return FormatRemaining(remaining);
        var totalHours = (int)Math.Ceiling(remaining.TotalHours);
        return $"{totalHours / 24}d {totalHours % 24:00}h";
    }

    /// <summary>"20m" or "1h 05m" for short gaps in sentences.</summary>
    public static string FormatShort(TimeSpan span)
    {
        var minutes = (int)Math.Max(1, Math.Round(span.TotalMinutes));
        return minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60:00}m";
    }

    public static string FormatClock(DateTimeOffset value) =>
        value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
