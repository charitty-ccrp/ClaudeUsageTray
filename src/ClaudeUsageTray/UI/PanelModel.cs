using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.UI;

internal enum StatusTone { None, Info, Warning, Error }

internal sealed record WindowRow(string Key, string Label, int Percent, UsageLevel Level, string Detail);

/// <summary>Everything the popup paints, as plain text/numbers, so the panel has no logic and the content is testable.</summary>
internal sealed record PanelModel(
    string Title,
    string Badge,
    WindowRow? Primary,
    string? BurnLine,
    bool BurnWarn,
    IReadOnlyList<WindowRow> Others,
    string? Status,
    StatusTone Tone,
    string Footer,
    string Summary)
{
    /// <summary>Changes exactly when something visible changes; lets the panel skip identical repaints.</summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>One-or-two-word problem description for the compact overlay, which has no room for the long status text.</summary>
    public string? ShortStatus { get; init; }

    public bool IsStale { get; init; }
}

internal static class PanelModelBuilder
{
    public static PanelModel Build(UsageState state, DateTimeOffset now, BurnRateTracker tracker, string badge,
        int? credentialAgeDays, bool refreshing)
    {
        WindowRow? primary = null;
        var others = new List<WindowRow>();
        string? burn = null;
        var burnWarn = false;

        if (state.Snapshot is { } snapshot)
        {
            foreach (var window in snapshot.Windows)
            {
                var percent = UsageMath.DisplayUtilization(window.Entry.Utilization);
                var level = UsageColor.Classify(percent);

                if (window.Key == UsageSnapshot.FiveHourKey)
                    primary = new WindowRow(window.Key, window.Label, percent, level, UsageText.PrimaryDetail(state, now));
                else
                    others.Add(new WindowRow(window.Key, window.Label, percent, level, UsageText.WindowDetail(window.Entry, now)));
            }

            var projection = tracker.Current(UsageSnapshot.FiveHourKey, now, snapshot.FiveHour);
            (burn, burnWarn) = UsageText.BurnLine(snapshot.FiveHour, now, projection, state.IsStale);
        }

        var (status, tone) = Status(state, credentialAgeDays);
        var footer = Footer(state, now, refreshing);
        var title = UsageText.PlanTitle(state);

        var model = new PanelModel(title, badge, primary, burn, burnWarn, others, status, tone, footer,
            Summary(title, primary, others))
        {
            ShortStatus = state.Error == UsageErrorKind.None ? null : UsageText.ShortError(state.Error),
            IsStale = state.IsStale,
        };
        return model with { Signature = Sign(model) };
    }

    public static (string? Text, StatusTone Tone) Status(UsageState state, int? credentialAgeDays)
    {
        if (state.Error != UsageErrorKind.None)
        {
            var text = string.IsNullOrWhiteSpace(state.ErrorMessage) ? UsageText.LongError(state.Error) : state.ErrorMessage;
            if (state.Snapshot is { } snapshot)
                text += $" Last good value: {UsageMath.DisplayUtilization(snapshot.FiveHour.Utilization)}%";
            return (text, StatusTone.Error);
        }
        if (state.Snapshot is null) return ("Waiting for first update...", StatusTone.Info);
        if (credentialAgeDays is >= 25)
            return ($"Credential saved {credentialAgeDays} days ago and may expire soon.", StatusTone.Warning);
        return (null, StatusTone.None);
    }

    public static string Footer(UsageState state, DateTimeOffset now, bool refreshing)
    {
        if (refreshing) return "Refreshing...";

        var head = state.LastSuccessfulUpdate is { } at
            ? (state.IsStale ? $"Last update {TimeFormatter.FormatClock(at)} (STALE)" : $"Updated {TimeFormatter.FormatClock(at)}")
            : "No data yet";

        if (state.NextAttemptAt is { } next)
        {
            var wait = next - now;
            var verb = state.Error == UsageErrorKind.None ? "next" : "retry";
            head += wait > TimeSpan.Zero ? $" · {verb} in {UsageText.Countdown(wait)}" : " · refreshing soon";
        }
        else if (state.Error is UsageErrorKind.Authentication or UsageErrorKind.NotConfigured)
        {
            head += " · waiting for you";
        }
        return head;
    }

    private static string Summary(string title, WindowRow? primary, IReadOnlyList<WindowRow> others)
    {
        var parts = new List<string> { title };
        if (primary is not null) parts.Add($"5-hour session {primary.Percent} percent. {primary.Detail}");
        parts.AddRange(others.Select(o => $"{o.Label.ToLowerInvariant()} {o.Percent} percent. {o.Detail}"));
        return string.Join(". ", parts);
    }

    private static string Sign(PanelModel m)
    {
        static string Row(WindowRow? r) => r is null ? "-" : $"{r.Key}:{r.Percent}:{r.Level}:{r.Detail}";
        return string.Join('', m.Title, m.Badge, Row(m.Primary), m.BurnLine, m.BurnWarn, string.Join('', m.Others.Select(Row)),
            m.Status, m.Tone, m.Footer, m.ShortStatus, m.IsStale);
    }
}
