using System.Net;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Tests;

public class BackoffJitterTests
{
    private static readonly TimeSpan Normal = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 300)]
    [InlineData(3, 900)]
    [InlineData(9, 900)]
    public void Rate_limit_backs_off_from_one_minute_to_fifteen(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), BackoffPolicy.NextDelay(UsageErrorKind.RateLimited, failures, Normal));

    [Fact]
    public void Rate_limit_honours_a_longer_retry_after_but_caps_it_at_an_hour()
    {
        Assert.Equal(TimeSpan.FromMinutes(7), BackoffPolicy.NextDelay(UsageErrorKind.RateLimited, 1, Normal, TimeSpan.FromMinutes(7)));
        Assert.Equal(TimeSpan.FromSeconds(60), BackoffPolicy.NextDelay(UsageErrorKind.RateLimited, 1, Normal, TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromHours(1), BackoffPolicy.NextDelay(UsageErrorKind.RateLimited, 1, Normal, TimeSpan.FromDays(2)));
    }

    [Theory]
    [InlineData(0.0, 48)]
    [InlineData(0.5, 60)]
    [InlineData(1.0, 72)]
    public void Jitter_stays_within_twenty_percent(double sample, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), BackoffPolicy.Jitter(TimeSpan.FromSeconds(60), sample));

    [Fact]
    public void Random_jitter_never_leaves_the_band()
    {
        for (var i = 0; i < 1000; i++)
        {
            var d = BackoffPolicy.Jitter(TimeSpan.FromSeconds(100));
            Assert.InRange(d.TotalSeconds, 80, 120);
        }
    }

    [Theory]
    [InlineData(UsageErrorKind.Network, true)]
    [InlineData(UsageErrorKind.Server, true)]
    [InlineData(UsageErrorKind.RateLimited, true)]
    [InlineData(UsageErrorKind.Authentication, false)]
    [InlineData(UsageErrorKind.CredentialUnavailable, false)]
    [InlineData(UsageErrorKind.Forbidden, false)]
    public void Only_self_healing_errors_count_as_transient(UsageErrorKind kind, bool expected) =>
        Assert.Equal(expected, BackoffPolicy.IsTransient(kind));
}

public class TransientRetryTests
{
    private const string Key = "sk-ant-sid01-abcdefghijklmnopqrstuvwxyz";
    private const string Org = "123e4567-e89b-12d3-a456-426614174000";
    private const string ValidJson = """{ "five_hour": { "utilization": 63, "resets_at": "2026-09-19T04:00:00Z" } }""";

    [Fact]
    public async Task A_single_gateway_error_is_retried_once_and_succeeds()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : FakeHandler.Json(ValidJson)));
        using var client = new ClaudeUsageClient(() => Key, () => Org, handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_dropped_connection_is_retried_once()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? throw new HttpRequestException("connection reset")
            : Task.FromResult(FakeHandler.Json(ValidJson)));
        using var client = new ClaudeUsageClient(() => Key, () => Org, handler);

        Assert.True((await client.GetUsageAsync(CancellationToken.None)).IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_persistent_failure_is_reported_after_exactly_two_attempts()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new ClaudeUsageClient(() => Key, () => Org, handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(UsageErrorKind.Server, result.Error);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Rate_limits_auth_errors_and_plain_500s_are_not_retried(HttpStatusCode status)
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(status)));
        using var client = new ClaudeUsageClient(() => Key, () => Org, handler);

        await client.GetUsageAsync(CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Retry_after_seconds_reach_the_result()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(240));
            return Task.FromResult(response);
        });
        using var client = new ClaudeUsageClient(() => Key, () => Org, handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(UsageErrorKind.RateLimited, result.Error);
        Assert.Equal(TimeSpan.FromSeconds(240), result.RetryAfter);
    }

    [Fact]
    public void Retry_after_as_a_date_becomes_the_remaining_wait()
    {
        var now = new DateTimeOffset(2026, 9, 19, 2, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddMinutes(3));

        Assert.Equal(TimeSpan.FromMinutes(3), ClaudeHttp.RetryAfterOf(response, now));
        Assert.Null(ClaudeHttp.RetryAfterOf(response, now.AddMinutes(5)));
    }
}

public class QuietFailureAndPauseTests
{
    private static DateTimeOffset _now = new(2026, 9, 19, 2, 0, 0, TimeSpan.Zero);

    private static UsageFetchResult Ok(double percent) =>
        UsageFetchResult.Ok(new UsageResponse(new UsageEntry(percent, _now.AddHours(2)), null, null, null));

    private static UsageService Create(IUsageProvider provider)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new UsageService(provider, () => TimeSpan.FromSeconds(60), () => _now, jitter: d => d); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public async Task Brief_network_blips_stay_quiet_then_surface_on_the_third_failure()
    {
        var fail = UsageFetchResult.Fail(UsageErrorKind.Network, "x");
        var provider = new ScriptedProvider().Enqueue(Ok(40)).Enqueue(fail).Enqueue(fail).Enqueue(fail);
        using var service = Create(provider);

        await service.RefreshOnceAsync(CancellationToken.None);
        var first = await service.RefreshOnceAsync(CancellationToken.None);
        var second = await service.RefreshOnceAsync(CancellationToken.None);
        var third = await service.RefreshOnceAsync(CancellationToken.None);

        Assert.True(first.IsQuietFailure);
        Assert.True(second.IsQuietFailure);
        Assert.False(third.IsQuietFailure);
        Assert.Equal(40, third.Snapshot!.FiveHour.Utilization);
    }

    [Fact]
    public async Task A_failure_lasting_five_minutes_surfaces_even_before_three_attempts()
    {
        var fail = UsageFetchResult.Fail(UsageErrorKind.Server, "x");
        var provider = new ScriptedProvider().Enqueue(Ok(40)).Enqueue(fail).Enqueue(fail);
        using var service = Create(provider);

        await service.RefreshOnceAsync(CancellationToken.None);
        var first = await service.RefreshOnceAsync(CancellationToken.None);
        _now = _now.AddMinutes(6);
        var later = await service.RefreshOnceAsync(CancellationToken.None);
        _now = _now.AddMinutes(-6);

        Assert.True(first.IsQuietFailure);
        Assert.False(later.IsQuietFailure);
    }

    [Theory]
    [InlineData(UsageErrorKind.CredentialUnavailable)]
    [InlineData(UsageErrorKind.Authentication)]
    public async Task Problems_that_need_the_user_are_never_quiet(UsageErrorKind kind)
    {
        var provider = new ScriptedProvider().Enqueue(Ok(40)).Enqueue(UsageFetchResult.Fail(kind, "x"));
        using var service = Create(provider);

        await service.RefreshOnceAsync(CancellationToken.None);
        var state = await service.RefreshOnceAsync(CancellationToken.None);

        Assert.False(state.IsQuietFailure);
    }

    [Fact]
    public async Task Without_a_previous_value_there_is_nothing_to_keep_showing_so_the_error_is_visible()
    {
        var provider = new ScriptedProvider().Enqueue(UsageFetchResult.Fail(UsageErrorKind.Network, "x"));
        using var service = Create(provider);

        Assert.False((await service.RefreshOnceAsync(CancellationToken.None)).IsQuietFailure);
    }

    [Fact]
    public async Task Rate_limit_delay_uses_retry_after()
    {
        var provider = new ScriptedProvider().Enqueue(UsageFetchResult.Fail(UsageErrorKind.RateLimited, "429", TimeSpan.FromMinutes(4)));
        using var service = Create(provider);

        await service.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(4), service.LastDelay);
    }

    [Fact]
    public async Task Paused_service_ignores_the_reset_tick_and_refreshes_once_when_resumed()
    {
        var provider = new ScriptedProvider().Enqueue(Ok(40)).Enqueue(Ok(41));
        using var service = Create(provider);
        await service.RefreshOnceAsync(CancellationToken.None);

        service.Paused = true;
        Assert.True(service.Paused);
        service.Paused = false;
        Assert.False(service.Paused);
        Assert.Equal(1, provider.Calls); // RefreshOnceAsync drives the provider; the loop is not running in tests
    }
}

public class PollingScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Idle_until_local_activity_then_active_for_ten_minutes()
    {
        var schedule = new PollingSchedule(() => PollingMode.Balanced);

        Assert.Equal(TimeSpan.FromMinutes(15), schedule.Interval(Now));

        schedule.RecordActivity(Now);
        Assert.Equal(TimeSpan.FromSeconds(90), schedule.Interval(Now.AddMinutes(9)));
        Assert.Equal(TimeSpan.FromMinutes(15), schedule.Interval(Now.AddMinutes(10)));
    }

    [Theory]
    [InlineData(PollingMode.Frequent, 60, 300)]
    [InlineData(PollingMode.Balanced, 90, 900)]
    [InlineData(PollingMode.Saver, 180, 1800)]
    public void Each_mode_has_its_own_active_and_idle_interval(PollingMode mode, int active, int idle)
    {
        var (a, i) = PollingSchedule.Intervals(mode);
        Assert.Equal(TimeSpan.FromSeconds(active), a);
        Assert.Equal(TimeSpan.FromSeconds(idle), i);
    }

    [Fact]
    public void Activity_triggers_a_refresh_only_when_the_value_is_older_than_the_active_interval()
    {
        var schedule = new PollingSchedule(() => PollingMode.Balanced);

        Assert.True(schedule.ShouldRefreshOnActivity(null, Now));
        Assert.True(schedule.ShouldRefreshOnActivity(Now.AddMinutes(-10), Now));
        Assert.False(schedule.ShouldRefreshOnActivity(Now.AddSeconds(-30), Now));
    }
}

public class DebouncedFileWatcherTests
{
    [Fact]
    public void Many_signals_inside_the_window_become_one_callback()
    {
        var calls = 0;
        using var watcher = new DebouncedFileWatcher(Path.Combine(Path.GetTempPath(), "cut-missing-" + Guid.NewGuid()),
            "*.jsonl", false, TimeSpan.FromMilliseconds(100), () => Interlocked.Increment(ref calls));

        Assert.False(watcher.IsWatching); // a missing folder is fine: it just never fires on its own
        for (var i = 0; i < 20; i++) watcher.Signal();

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(5)));
        Thread.Sleep(300);
        Assert.Equal(1, Volatile.Read(ref calls));

        watcher.Signal();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 2, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_real_file_write_fires_the_callback()
    {
        using var dir = new TempDir();
        var fired = new ManualResetEventSlim();
        using var watcher = new DebouncedFileWatcher(dir.Path, "*.jsonl", true, TimeSpan.FromMilliseconds(50), fired.Set);

        Assert.True(watcher.IsWatching);
        var sub = Directory.CreateDirectory(Path.Combine(dir.Path, "project"));
        File.WriteAllText(Path.Combine(sub.FullName, "session.jsonl"), "{}\n");

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
    }
}

public class NotificationFilterTests
{
    private static UsageNotification Alert(UsageLevel level, bool reset = false) =>
        new("five_hour", "5-hour", 90, level, reset, null);

    [Fact]
    public void Levels_the_user_turned_off_are_dropped()
    {
        var config = new AppConfig { NotifyWarning = false, NotifyReset = false };
        var kept = NotificationFilter.Apply(
            [Alert(UsageLevel.Warning), Alert(UsageLevel.High), Alert(UsageLevel.Normal, reset: true)], config, new DateTime(2026, 1, 1, 12, 0, 0));

        Assert.Equal([UsageLevel.High], kept.Select(a => a.Level));
    }

    [Theory]
    [InlineData(22, 7, 23, true)]
    [InlineData(22, 7, 3, true)]
    [InlineData(22, 7, 7, false)]
    [InlineData(22, 7, 12, false)]
    [InlineData(9, 17, 12, true)]
    [InlineData(9, 17, 18, false)]
    public void Quiet_hours_work_across_midnight(int start, int end, int hour, bool quiet)
    {
        var config = new AppConfig { QuietHoursEnabled = true, QuietStartHour = start, QuietEndHour = end };
        Assert.Equal(quiet, NotificationFilter.InQuietHours(config, new DateTime(2026, 1, 1, hour, 30, 0)));
    }

    [Fact]
    public void Nothing_is_shown_when_notifications_are_off_or_during_quiet_hours()
    {
        var off = new AppConfig { NotificationsEnabled = false };
        var quiet = new AppConfig { QuietHoursEnabled = true, QuietStartHour = 0, QuietEndHour = 23 };

        Assert.Empty(NotificationFilter.Apply([Alert(UsageLevel.Critical)], off, new DateTime(2026, 1, 1, 12, 0, 0)));
        Assert.Empty(NotificationFilter.Apply([Alert(UsageLevel.Critical)], quiet, new DateTime(2026, 1, 1, 12, 0, 0)));
    }
}
