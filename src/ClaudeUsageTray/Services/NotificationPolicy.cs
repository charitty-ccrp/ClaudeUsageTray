using ClaudeUsageTray.Models;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Services;

public sealed record UsageNotification(
    string WindowKey, string Label, int Percent, UsageLevel Level, bool IsReset, DateTimeOffset? ResetsAt);

/// <summary>
/// Decides which alerts to raise. Reuses UsageColor thresholds so alerts match the icon colour. Alerts only fire when
/// a window's level goes UP, never on the first reading (that becomes a silent baseline), never for stale/failed data,
/// and a reset is announced only if the user had been warned about that window.
/// </summary>
public sealed class NotificationPolicy
{
    private static readonly TimeSpan ResetTolerance = TimeSpan.FromMinutes(2);
    private const double DropThreshold = 20; // a real reset falls to ~0; a few points down is not a reset

    private sealed class Track
    {
        public DateTimeOffset? ResetsAt;
        public UsageLevel Notified;
        public double LastUtilization;
    }

    private readonly Dictionary<string, Track> _tracks = [];

    public IReadOnlyList<UsageNotification> Evaluate(UsageState state)
    {
        var result = new List<UsageNotification>();
        if (state.Snapshot is null || state.IsStale || state.Error != UsageErrorKind.None) return result;

        foreach (var window in state.Snapshot.Windows)
        {
            var entry = window.Entry;
            var percent = UsageMath.DisplayUtilization(entry.Utilization);
            var level = UsageColor.Classify(percent);

            if (!_tracks.TryGetValue(window.Key, out var track))
            {
                _tracks[window.Key] = new Track { ResetsAt = entry.ResetsAt, Notified = level, LastUtilization = entry.Utilization };
                continue;
            }

            var windowReset = ResetTimeChanged(track.ResetsAt, entry.ResetsAt) ||
                              entry.Utilization < track.LastUtilization - DropThreshold;

            if (windowReset)
            {
                if (track.Notified >= UsageLevel.Warning)
                    result.Add(new UsageNotification(window.Key, window.Label, percent, level, true, entry.ResetsAt));
                track.Notified = level;
            }
            else if (level > track.Notified)
            {
                result.Add(new UsageNotification(window.Key, window.Label, percent, level, false, entry.ResetsAt));
                track.Notified = level;
            }
            else if (level < track.Notified)
            {
                track.Notified = level; // allow the same level to alert again if usage climbs back
            }

            track.ResetsAt = entry.ResetsAt;
            track.LastUtilization = entry.Utilization;
        }
        return result;
    }

    private static bool ResetTimeChanged(DateTimeOffset? previous, DateTimeOffset? current) =>
        previous is { } a && current is { } b && (a - b).Duration() > ResetTolerance;
}
