using ClaudeUsageTray.Models;

namespace ClaudeUsageTray.Services;

/// <summary>
/// Chooses how often to ask Claude for usage. The percentage only moves while Claude is being used, so the app
/// polls often while Claude Code is active on this PC (its transcript files are being written) and rarely otherwise.
/// Usage from claude.ai or other devices is still picked up by the slower idle poll. Pure and clock-driven for tests.
/// </summary>
public sealed class PollingSchedule(Func<PollingMode> mode)
{
    /// <summary>How long after the last local activity the app still counts as "active".</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(10);

    private long _lastActivityTicks = DateTimeOffset.MinValue.UtcTicks;

    public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    public void RecordActivity(DateTimeOffset now) => Interlocked.Exchange(ref _lastActivityTicks, now.UtcTicks);

    public bool IsActive(DateTimeOffset now) => now - LastActivity < ActiveWindow;

    public TimeSpan Interval(DateTimeOffset now)
    {
        var (active, idle) = Intervals(mode());
        return IsActive(now) ? active : idle;
    }

    /// <summary>True when local activity means the last value is older than the active interval and worth refreshing now.</summary>
    public bool ShouldRefreshOnActivity(DateTimeOffset? lastSuccess, DateTimeOffset now) =>
        lastSuccess is not { } last || now - last >= Intervals(mode()).Active;

    public static (TimeSpan Active, TimeSpan Idle) Intervals(PollingMode mode) => mode switch
    {
        PollingMode.Frequent => (TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5)),
        PollingMode.Saver => (TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(30)),
        _ => (TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(15)),
    };
}
