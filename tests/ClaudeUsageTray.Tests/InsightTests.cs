using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Tests;

public class UsageSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 2, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot(double fiveHour, double? week = null, double? opus = null, double? sonnet = null) =>
        new(new UsageResponse(
            new UsageEntry(fiveHour, Now.AddHours(3)),
            week is null ? null : new UsageEntry(week.Value, Now.AddDays(2)),
            opus is null ? null : new UsageEntry(opus.Value, Now.AddDays(2)),
            sonnet is null ? null : new UsageEntry(sonnet.Value, Now.AddDays(2))), Now);

    [Fact]
    public void Windows_lists_only_what_claude_returned_five_hour_first()
    {
        Assert.Single(Snapshot(10).Windows);
        Assert.Equal(["five_hour", "seven_day", "seven_day_opus"], Snapshot(10, 20, 30).Windows.Select(w => w.Key));
    }

}

public class BurnRateTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = T0.AddHours(5);

    private static UsageEntry E(double utilization, DateTimeOffset? reset = null) => new(utilization, reset ?? Reset);

    [Fact]
    public void Returns_null_without_enough_samples_or_time()
    {
        var tracker = new BurnRateTracker();
        Assert.Null(tracker.Current("five_hour", T0, E(10)));

        tracker.Add("five_hour", T0, E(10));
        Assert.Null(tracker.Current("five_hour", T0, E(10)));

        tracker.Add("five_hour", T0.AddMinutes(4), E(12));
        Assert.Null(tracker.Current("five_hour", T0.AddMinutes(4), E(12)));
    }

    [Fact]
    public void Estimates_rate_and_time_to_full()
    {
        var tracker = new BurnRateTracker();
        tracker.Add("five_hour", T0, E(10));
        tracker.Add("five_hour", T0.AddMinutes(30), E(20));
        tracker.Add("five_hour", T0.AddMinutes(60), E(30));

        var now = T0.AddMinutes(60);
        var projection = tracker.Current("five_hour", now, E(30));

        Assert.NotNull(projection);
        Assert.Equal(20, projection!.RatePerHour, 3);
        Assert.Equal(now.AddHours(3.5), projection.FullAt);
    }

    [Fact]
    public void Flat_or_slow_usage_is_not_projected_to_fill()
    {
        var tracker = new BurnRateTracker();
        tracker.Add("five_hour", T0, E(30));
        tracker.Add("five_hour", T0.AddMinutes(30), E(30));
        tracker.Add("five_hour", T0.AddMinutes(60), E(30));

        var projection = tracker.Current("five_hour", T0.AddMinutes(60), E(30));

        Assert.NotNull(projection);
        Assert.Null(projection!.FullAt);
    }

    [Fact]
    public void Reset_time_change_starts_a_fresh_history()
    {
        var tracker = new BurnRateTracker();
        tracker.Add("five_hour", T0, E(80));
        tracker.Add("five_hour", T0.AddMinutes(30), E(90));

        tracker.Add("five_hour", T0.AddMinutes(60), E(2, Reset.AddHours(5)));

        Assert.Equal(1, tracker.SampleCount("five_hour"));
    }

    [Fact]
    public void Small_jitter_in_reset_time_is_not_a_new_window()
    {
        var tracker = new BurnRateTracker();
        tracker.Add("five_hour", T0, E(10));
        tracker.Add("five_hour", T0.AddMinutes(10), E(12, Reset.AddSeconds(1)));

        Assert.Equal(2, tracker.SampleCount("five_hour"));
    }

    [Fact]
    public void Sharp_drop_without_a_reset_time_change_clears_history()
    {
        var tracker = new BurnRateTracker();
        tracker.Add("five_hour", T0, E(80));
        tracker.Add("five_hour", T0.AddMinutes(10), E(10));

        Assert.Equal(1, tracker.SampleCount("five_hour"));
    }

    [Fact]
    public void History_is_bounded_and_windows_are_independent()
    {
        var tracker = new BurnRateTracker();
        for (var i = 0; i < BurnRateTracker.MaxSamples + 30; i++)
            tracker.Add("five_hour", T0.AddMinutes(i), E(10 + i * 0.01));
        tracker.Add("seven_day", T0, E(5));

        Assert.Equal(BurnRateTracker.MaxSamples, tracker.SampleCount("five_hour"));
        Assert.Equal(1, tracker.SampleCount("seven_day"));
    }

    [Fact]
    public void Duplicate_or_out_of_order_timestamps_are_ignored()
    {
        var tracker = new BurnRateTracker();
        tracker.Add("five_hour", T0, E(10));
        tracker.Add("five_hour", T0, E(11));
        tracker.Add("five_hour", T0.AddMinutes(-5), E(9));

        Assert.Equal(1, tracker.SampleCount("five_hour"));
    }

    [Fact]
    public void At_limit_reports_full_now()
    {
        var tracker = new BurnRateTracker();
        tracker.Add("five_hour", T0, E(90));
        tracker.Add("five_hour", T0.AddMinutes(30), E(100));

        var now = T0.AddMinutes(30);
        Assert.Equal(now, tracker.Current("five_hour", now, E(100))!.FullAt);
    }
}

public class NotificationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = Now.AddHours(3);

    private static UsageState State(double fiveHour, double? week = null, DateTimeOffset? reset = null, bool stale = false,
        UsageErrorKind error = UsageErrorKind.None)
    {
        var response = new UsageResponse(
            new UsageEntry(fiveHour, reset ?? Reset),
            week is null ? null : new UsageEntry(week.Value, Now.AddDays(3)),
            null, null);
        return new UsageState(new UsageSnapshot(response, Now), stale, error, null, Now, null);
    }

    [Fact]
    public void First_reading_is_a_silent_baseline_even_when_high()
    {
        var policy = new NotificationPolicy();
        Assert.Empty(policy.Evaluate(State(92)));
    }

    [Fact]
    public void Alerts_only_when_the_level_rises()
    {
        var policy = new NotificationPolicy();
        policy.Evaluate(State(10));

        Assert.Empty(policy.Evaluate(State(60)));

        var warning = Assert.Single(policy.Evaluate(State(72)));
        Assert.Equal(UsageLevel.Warning, warning.Level);
        Assert.False(warning.IsReset);

        Assert.Empty(policy.Evaluate(State(75)));            // same level again
        Assert.Equal(UsageLevel.High, Assert.Single(policy.Evaluate(State(86))).Level);
        Assert.Equal(UsageLevel.Critical, Assert.Single(policy.Evaluate(State(96))).Level);
        Assert.Empty(policy.Evaluate(State(99)));
    }

    [Fact]
    public void Jumping_several_levels_raises_one_alert_at_the_new_level()
    {
        var policy = new NotificationPolicy();
        policy.Evaluate(State(10));

        var alert = Assert.Single(policy.Evaluate(State(97)));
        Assert.Equal(UsageLevel.Critical, alert.Level);
    }

    [Fact]
    public void Stale_or_failed_data_never_alerts()
    {
        var policy = new NotificationPolicy();
        policy.Evaluate(State(10));

        Assert.Empty(policy.Evaluate(State(90, stale: true, error: UsageErrorKind.Network)));
        Assert.Empty(policy.Evaluate(new UsageState(null, false, UsageErrorKind.Authentication, "x", null, null)));
    }

    [Fact]
    public void Announces_a_reset_only_after_the_user_was_warned()
    {
        var policy = new NotificationPolicy();
        policy.Evaluate(State(10));
        policy.Evaluate(State(88));

        var afterReset = policy.Evaluate(State(1, reset: Reset.AddHours(5)));

        var notice = Assert.Single(afterReset);
        Assert.True(notice.IsReset);
        Assert.Equal("five_hour", notice.WindowKey);

        // usage low again then a later reset stays quiet
        policy.Evaluate(State(20, reset: Reset.AddHours(5)));
        Assert.Empty(policy.Evaluate(State(1, reset: Reset.AddHours(10))));
    }

    [Fact]
    public void Alerts_can_repeat_after_usage_falls_back_below_a_level_without_a_reset()
    {
        var policy = new NotificationPolicy();
        policy.Evaluate(State(10));
        policy.Evaluate(State(75));
        policy.Evaluate(State(68));

        Assert.Single(policy.Evaluate(State(72)));
    }

    [Fact]
    public void Windows_are_tracked_independently()
    {
        var policy = new NotificationPolicy();
        policy.Evaluate(State(10, week: 10));

        var alerts = policy.Evaluate(State(10, week: 90));

        var weekly = Assert.Single(alerts);
        Assert.Equal("seven_day", weekly.WindowKey);
    }
}

public class UsageServiceExtrasTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 2, 0, 0, TimeSpan.Zero);

    private sealed class OneShot(UsageFetchResult result) : IUsageProvider
    {
        public Task<UsageFetchResult> GetUsageAsync(CancellationToken cancellationToken) => Task.FromResult(result);
        public Task<string?> GetPlanNameAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private static UsageService Create(IUsageProvider provider)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new UsageService(provider, () => TimeSpan.FromSeconds(60), () => Now); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public async Task Refreshing_flag_is_raised_around_each_refresh_and_cleared_on_failure()
    {
        var ok = UsageFetchResult.Ok(new UsageResponse(new UsageEntry(10, Now.AddHours(1)), null, null, null));
        using var service = Create(new OneShot(ok));
        var seen = new List<bool>();
        service.RefreshingChanged += seen.Add;

        await service.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal([true, false], seen);
        Assert.False(service.IsRefreshing);
    }

    [Fact]
    public async Task Successful_refresh_feeds_every_window_to_the_burn_rate_tracker()
    {
        var response = new UsageResponse(new UsageEntry(10, Now.AddHours(1)), new UsageEntry(5, Now.AddDays(2)), null, null);
        using var service = Create(new OneShot(UsageFetchResult.Ok(response)));

        await service.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(1, service.BurnRates.SampleCount("five_hour"));
        Assert.Equal(1, service.BurnRates.SampleCount("seven_day"));
    }
}
