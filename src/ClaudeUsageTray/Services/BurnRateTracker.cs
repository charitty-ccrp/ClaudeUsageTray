using ClaudeUsageTray.Models;

namespace ClaudeUsageTray.Services;

/// <summary>Estimated consumption rate. FullAt is null when usage is not on track to reach 100%.</summary>
public sealed record BurnProjection(double RatePerHour, DateTimeOffset? FullAt);

/// <summary>
/// In-memory linear estimate of how fast a window is filling up. This is our own extrapolation of recent samples,
/// not a value Claude reports, and it starts over whenever the app restarts or the window resets.
/// </summary>
public sealed class BurnRateTracker
{
    public const int MaxSamples = 120;
    public const double MinRatePerHour = 0.5;
    public static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ResetTolerance = TimeSpan.FromMinutes(2);
    private const double DropThreshold = 5;
    private static readonly TimeSpan MaxHorizon = TimeSpan.FromDays(30);

    private sealed class History
    {
        public DateTimeOffset? ResetsAt;
        public readonly List<(DateTimeOffset At, double Utilization)> Samples = [];
    }

    private readonly Dictionary<string, History> _histories = [];
    private readonly object _gate = new();

    public void Add(string key, DateTimeOffset at, UsageEntry entry)
    {
        lock (_gate)
        {
            if (!_histories.TryGetValue(key, out var history))
            {
                history = new History { ResetsAt = entry.ResetsAt };
                _histories[key] = history;
            }
            else if (!SameWindow(history.ResetsAt, entry.ResetsAt) ||
                     (history.Samples.Count > 0 && entry.Utilization < history.Samples[^1].Utilization - DropThreshold))
            {
                // a new window started (or usage fell sharply without the reset time changing)
                history.Samples.Clear();
                history.ResetsAt = entry.ResetsAt;
            }

            if (history.Samples.Count > 0 && history.Samples[^1].At >= at) return;

            history.Samples.Add((at, entry.Utilization));
            if (history.Samples.Count > MaxSamples) history.Samples.RemoveAt(0);
        }
    }

    public int SampleCount(string key)
    {
        lock (_gate) return _histories.TryGetValue(key, out var history) ? history.Samples.Count : 0;
    }

    /// <summary>Null until there are at least two samples spanning MinSpan.</summary>
    public BurnProjection? Current(string key, DateTimeOffset now, UsageEntry entry)
    {
        double rate;
        lock (_gate)
        {
            if (!_histories.TryGetValue(key, out var history) || history.Samples.Count < 2) return null;
            var samples = history.Samples;
            if (samples[^1].At - samples[0].At < MinSpan) return null;

            var origin = samples[0].At;
            var meanT = samples.Average(s => (s.At - origin).TotalHours);
            var meanU = samples.Average(s => s.Utilization);
            double sxx = 0, sxy = 0;
            foreach (var (at, utilization) in samples)
            {
                var dt = (at - origin).TotalHours - meanT;
                sxx += dt * dt;
                sxy += dt * (utilization - meanU);
            }
            if (sxx <= 0) return null;
            rate = sxy / sxx;
        }

        rate = Math.Max(rate, 0);
        if (entry.Utilization >= 100) return new BurnProjection(rate, now);
        if (rate <= MinRatePerHour) return new BurnProjection(rate, null);

        var untilFull = TimeSpan.FromHours((100 - entry.Utilization) / rate);
        return new BurnProjection(rate, untilFull > MaxHorizon ? null : now + untilFull);
    }

    private static bool SameWindow(DateTimeOffset? a, DateTimeOffset? b) =>
        (a is null && b is null) || (a is { } x && b is { } y && (x - y).Duration() <= ResetTolerance);
}
