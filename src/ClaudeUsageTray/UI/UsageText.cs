using ClaudeUsageTray.App;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.UI;

/// <summary>All user-facing strings derived from UsageState, so tray, menu and popup stay consistent.</summary>
internal static class UsageText
{
    // NotifyIcon.Text accepts up to 127 characters on current .NET (63 on .NET Framework); we target .NET 10
    private const int TooltipLimit = 127;

    public static string PlanTitle(UsageState state) =>
        string.IsNullOrWhiteSpace(state.Snapshot?.PlanName) ? "Claude" : $"Claude {state.Snapshot!.PlanName}";

    public static string ShortError(UsageErrorKind kind) => kind switch
    {
        UsageErrorKind.NotConfigured => "Setup needed",
        UsageErrorKind.Authentication => "Sign-in expired",
        UsageErrorKind.Forbidden => "Access blocked",
        UsageErrorKind.Server => "Claude is unavailable",
        UsageErrorKind.InvalidResponse => "Unexpected response",
        UsageErrorKind.Network => "No connection",
        UsageErrorKind.CredentialUnavailable => "Claude Code login needed",
        UsageErrorKind.RateLimited => "Checking too often",
        _ => string.Empty,
    };

    public static string LongError(UsageErrorKind kind) => kind switch
    {
        UsageErrorKind.NotConfigured => "Setup isn't finished. Open Settings and choose where usage comes from.",
        UsageErrorKind.Authentication => "Your claude.ai sign-in expired. Open Settings and paste a new session cookie.",
        UsageErrorKind.Forbidden => "Claude rejected the request. This app tries again in a few minutes.",
        UsageErrorKind.Server => "Claude isn't responding right now. This app keeps trying.",
        UsageErrorKind.InvalidResponse => "Claude sent something unexpected. The usage endpoint may have changed.",
        UsageErrorKind.Network => "Can't reach Claude. Check your internet connection; this app keeps trying.",
        UsageErrorKind.CredentialUnavailable => "Your Claude Code login is missing or expired. Open Claude Code or the Claude app once and this app reconnects by itself.",
        UsageErrorKind.RateLimited => "Claude asked this app to slow down. It waits a few minutes and tries again.",
        _ => string.Empty,
    };

    public static string ResetText(UsageState state, DateTimeOffset now, bool withClock)
    {
        var reset = state.Snapshot?.FiveHour.ResetsAt;
        if (reset is null) return state.Snapshot is null ? "--" : "unavailable";

        var remaining = reset.Value - now;
        if (remaining <= TimeSpan.Zero) return "refreshing...";
        if (remaining > AppConstants.MaxPlausibleReset) return "check PC clock";

        var text = TimeFormatter.FormatRemaining(remaining);
        return withClock ? $"{text} (at {reset.Value.ToLocalTime():HH:mm})" : text;
    }

    /// <summary>"18% - resets in 2d 05h (Sat 16:59)" for a weekly window; null when Claude did not report it.</summary>
    public static string? WeeklyText(UsageEntry? entry, DateTimeOffset now)
    {
        if (entry is null) return null;

        var percent = $"{UsageMath.DisplayUtilization(entry.Utilization)}%";
        if (entry.ResetsAt is not { } reset) return percent;

        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero) return $"{percent} - refreshing...";

        var at = reset.ToLocalTime().ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return $"{percent} - resets in {TimeFormatter.FormatRemainingLong(remaining)} ({at})";
    }

    // ---------- popup detail lines ----------

    private const string ResetUnavailable = "unavailable";
    private const string ResetRefreshing = "refreshing...";
    private const string ResetClockWrong = "check PC clock";

    /// <summary>Second line under the 5-hour bar: "Resets in 03h 15m (at 14:00)".</summary>
    public static string PrimaryDetail(UsageState state, DateTimeOffset now)
    {
        var text = ResetText(state, now, true);
        return text switch
        {
            "--" => string.Empty,
            ResetUnavailable => "Reset time unavailable",
            ResetRefreshing => "Refreshing...",
            ResetClockWrong => "Check PC clock - the reset time looks wrong",
            _ => $"Resets in {text}",
        };
    }

    /// <summary>"resets in 2d 05h (Sat 17:00)" for the smaller weekly rows; empty when Claude gave no reset time.</summary>
    public static string WindowDetail(UsageEntry entry, DateTimeOffset now)
    {
        if (entry.ResetsAt is not { } reset) return string.Empty;
        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero) return "Refreshing...";
        var at = reset.ToLocalTime().ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return $"Resets in {TimeFormatter.FormatRemainingLong(remaining)} ({at})";
    }

    /// <summary>The "could run out by" line. Text is null when there is nothing useful to say (stale data, limit reached).</summary>
    public static (string? Text, bool Warn) BurnLine(UsageEntry entry, DateTimeOffset now, BurnProjection? projection, bool stale)
    {
        if (stale || entry.Utilization >= 100) return (null, false);
        if (projection is null) return ("Collecting usage rate...", false);
        if (projection.RatePerHour <= BurnRateTracker.MinRatePerHour)
            return ("Usage is steady - not on track to hit the limit", false);

        var rate = $"~{projection.RatePerHour:0}%/h";
        if (projection.FullAt is not { } fullAt) return ($"{rate} - not on track to hit the limit", false);

        var clock = fullAt.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (entry.ResetsAt is { } reset)
        {
            return fullAt >= reset
                ? ($"{rate} - resets before you run out", false)
                : ($"{rate} - could run out by {clock}, {TimeFormatter.FormatShort(reset - fullAt)} before reset", true);
        }
        return ($"{rate} - could run out by {clock}", true);
    }

    /// <summary>The between-polls estimate, always labelled; null unless it is above the real figure.</summary>
    public static string? EstimateLine(int realPercent, int? estimatePercent) =>
        estimatePercent is { } est && est > realPercent ? $"≈ {est}% (est.) from local Claude Code use since the last check" : null;

    public static string Countdown(TimeSpan span) =>
        span.TotalSeconds < 120 ? $"{Math.Max(1, (int)Math.Ceiling(span.TotalSeconds))}s" : $"{(int)Math.Ceiling(span.TotalMinutes)}m";

    // ---------- toast ----------

    public sealed record ToastContent(string Title, string Text, bool Warn);

    public static string FriendlyName(string windowKey) => windowKey switch
    {
        UsageSnapshot.FiveHourKey => "5-hour",
        UsageSnapshot.SevenDayKey => "Weekly",
        UsageSnapshot.SevenDayOpusKey => "Weekly Opus",
        UsageSnapshot.SevenDaySonnetKey => "Weekly Sonnet",
        _ => windowKey,
    };

    public static ToastContent Toast(IReadOnlyList<UsageNotification> items, DateTimeOffset now)
    {
        if (items.Count == 1) return Single(items[0], now);

        var lines = items.Select(item => Single(item, now).Title);
        return new ToastContent("Claude usage alert", string.Join("\n", lines), items.Any(i => !i.IsReset));
    }

    private static ToastContent Single(UsageNotification item, DateTimeOffset now)
    {
        var name = FriendlyName(item.WindowKey);
        if (item.IsReset)
            return new ToastContent($"Claude {name} limit reset", $"Usage is back to {item.Percent}%.", false);

        var phrase = item.Level switch
        {
            UsageLevel.Critical => "Almost out of usage.",
            UsageLevel.High => "Running low - consider slowing down.",
            _ => "Usage is getting high.",
        };
        var suffix = item.ResetsAt is { } reset && reset > now
            ? $" Resets in {TimeFormatter.FormatRemainingLong(reset - now)}."
            : string.Empty;
        return new ToastContent($"Claude {name} usage at {item.Percent}%", phrase + suffix, true);
    }

    // ---------- tray tooltip ----------

    public static string Tooltip(UsageState state, DateTimeOffset now, int? estimatePercent = null)
    {
        var snapshot = state.Snapshot;
        if (snapshot is null)
        {
            var reason = state.Error == UsageErrorKind.None ? "Waiting for data" : ShortError(state.Error);
            return Truncate($"Claude --\n{reason}");
        }

        // the tray icon is the 5-hour figure; weekly is appended when Claude reports it so one hover tells both
        var head = $"Claude {UsageMath.DisplayUtilization(snapshot.FiveHour.Utilization)}%";
        if (snapshot.Usage.SevenDay is { } week)
            head += $" 5h · {UsageMath.DisplayUtilization(week.Utilization)}% wk";
        if (state.IsStale && !state.IsQuietFailure) head += " (last known)";

        var resetLine = $"Reset: {ResetText(state, now, false)}";
        if (estimatePercent is { } est && est > UsageMath.DisplayUtilization(snapshot.FiveHour.Utilization))
            resetLine = $"≈ {est}% (est.) · {resetLine}";

        var updated = state.LastSuccessfulUpdate is { } at ? TimeFormatter.FormatClock(at) : "--";
        var updatedLabel = state.IsStale && !state.IsQuietFailure ? "Last update" : "Updated";
        return Truncate($"{head}\n{resetLine}\n{updatedLabel}: {updated}");
    }

    private static string Truncate(string text) =>
        text.Length <= TooltipLimit ? text : text[..TooltipLimit];
}
