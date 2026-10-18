using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.UI;

namespace ClaudeUsageTray.Tests;

internal sealed class ScriptedProvider : IUsageProvider
{
    private readonly Queue<UsageFetchResult> _results = new();
    public int Calls { get; private set; }
    public string? Plan { get; set; } = "Pro";

    public ScriptedProvider Enqueue(UsageFetchResult result)
    {
        _results.Enqueue(result);
        return this;
    }

    public Task<UsageFetchResult> GetUsageAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_results.Dequeue());
    }

    public Task<string?> GetPlanNameAsync(CancellationToken cancellationToken) => Task.FromResult(Plan);
}

public class UsageServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 2, 0, 0, TimeSpan.Zero);

    private static UsageFetchResult Ok(double percent, DateTimeOffset? reset = null) =>
        UsageFetchResult.Ok(new UsageResponse(new UsageEntry(percent, reset ?? Now.AddHours(2)), null, null, null));

    private static UsageService Create(ScriptedProvider provider) => Create((IUsageProvider)provider);

    // xUnit installs its own SynchronizationContext; the service captures Current, so clear it to get synchronous events.
    private static UsageService Create(IUsageProvider provider)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new UsageService(provider, () => TimeSpan.FromSeconds(60), () => Now, jitter: d => d); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public async Task Success_publishes_snapshot_with_plan_and_normal_delay()
    {
        var provider = new ScriptedProvider().Enqueue(Ok(63));
        using var service = Create(provider);

        var state = await service.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(63, state.Snapshot!.FiveHour.Utilization);
        Assert.Equal("Pro", state.Snapshot.PlanName);
        Assert.False(state.IsStale);
        Assert.Equal(UsageErrorKind.None, state.Error);
        Assert.Equal(Now, state.LastSuccessfulUpdate);
        Assert.Equal(TimeSpan.FromSeconds(60), service.LastDelay);
    }

    [Fact]
    public async Task Failure_keeps_last_good_value_as_stale_never_zero()
    {
        var provider = new ScriptedProvider()
            .Enqueue(Ok(63))
            .Enqueue(UsageFetchResult.Fail(UsageErrorKind.Network, "Network unavailable."));
        using var service = Create(provider);

        await service.RefreshOnceAsync(CancellationToken.None);
        var state = await service.RefreshOnceAsync(CancellationToken.None);

        Assert.True(state.IsStale);
        Assert.Equal(63, state.Snapshot!.FiveHour.Utilization);
        Assert.Equal(UsageErrorKind.Network, state.Error);
        Assert.Equal(TimeSpan.FromSeconds(15), service.LastDelay);
    }

    [Fact]
    public async Task Failure_without_previous_value_is_not_stale_and_has_no_snapshot()
    {
        var provider = new ScriptedProvider().Enqueue(UsageFetchResult.Fail(UsageErrorKind.Server, "HTTP 503"));
        using var service = Create(provider);

        var state = await service.RefreshOnceAsync(CancellationToken.None);

        Assert.Null(state.Snapshot);
        Assert.False(state.IsStale);
    }

    [Fact]
    public async Task Backoff_grows_then_resets_after_success()
    {
        var fail = UsageFetchResult.Fail(UsageErrorKind.Network, "x");
        var provider = new ScriptedProvider().Enqueue(fail).Enqueue(fail).Enqueue(fail).Enqueue(fail).Enqueue(Ok(10));
        using var service = Create(provider);

        var delays = new List<TimeSpan?>();
        for (var i = 0; i < 5; i++)
        {
            await service.RefreshOnceAsync(CancellationToken.None);
            delays.Add(service.LastDelay);
        }

        Assert.Equal(
            [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(60)],
            delays);
    }

    [Fact]
    public async Task Authentication_error_stops_automatic_retry_but_keeps_value_stale()
    {
        var provider = new ScriptedProvider()
            .Enqueue(Ok(40))
            .Enqueue(UsageFetchResult.Fail(UsageErrorKind.Authentication, "401"));
        using var service = Create(provider);

        await service.RefreshOnceAsync(CancellationToken.None);
        var state = await service.RefreshOnceAsync(CancellationToken.None);

        Assert.Null(service.LastDelay);
        Assert.Null(state.NextAttemptAt);
        Assert.True(state.IsStale);
        Assert.Equal(40, state.Snapshot!.FiveHour.Utilization);
    }

    [Fact]
    public async Task Forbidden_uses_long_backoff()
    {
        var provider = new ScriptedProvider().Enqueue(UsageFetchResult.Fail(UsageErrorKind.Forbidden, "403"));
        using var service = Create(provider);

        var state = await service.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(5), service.LastDelay);
        Assert.Equal(Now.AddMinutes(5), state.NextAttemptAt);
    }

    [Fact]
    public async Task Provider_exception_becomes_error_state_instead_of_crashing()
    {
        using var service = Create(new ThrowingProvider());

        var state = await service.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(UsageErrorKind.InvalidResponse, state.Error);
    }

    [Fact]
    public async Task Events_are_raised_for_each_refresh()
    {
        var provider = new ScriptedProvider().Enqueue(Ok(1)).Enqueue(Ok(2));
        using var service = Create(provider);
        var seen = new List<double>();
        service.StateChanged += s => seen.Add(s.Snapshot!.FiveHour.Utilization);

        await service.RefreshOnceAsync(CancellationToken.None);
        await service.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal([1.0, 2.0], seen);
    }

    private sealed class ThrowingProvider : IUsageProvider
    {
        public Task<UsageFetchResult> GetUsageAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
        public Task<string?> GetPlanNameAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}

public class UsageTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 2, 0, 0, TimeSpan.Zero);

    private static UsageState StateWith(double percent, DateTimeOffset? reset, bool stale = false, UsageErrorKind error = UsageErrorKind.None) =>
        new(new UsageSnapshot(new UsageResponse(new UsageEntry(percent, reset), null, null, null), Now, "Pro"),
            stale, error, null, Now, null);

    [Fact]
    public void Tooltip_shows_percent_reset_and_update_time_within_limit()
    {
        var text = UsageText.Tooltip(StateWith(63, Now.AddMinutes(137)), Now);

        Assert.StartsWith("Claude 63%\nReset: 02h 17m\nUpdated: ", text);
        Assert.True(text.Length <= 63);
    }

    [Fact]
    public void Stale_tooltip_says_stale()
    {
        var text = UsageText.Tooltip(StateWith(63, Now.AddHours(1), stale: true, error: UsageErrorKind.Network), Now);

        Assert.Contains("(last known)", text);
        Assert.Contains("Last update:", text);
    }

    [Fact]
    public void Tooltip_without_data_explains_why()
    {
        var text = UsageText.Tooltip(new UsageState(null, false, UsageErrorKind.Authentication, "x", null, null), Now);

        Assert.Equal("Claude --\nSign-in expired", text);
    }

    [Fact]
    public void Reset_text_handles_missing_past_and_implausible_times()
    {
        Assert.Equal("unavailable", UsageText.ResetText(StateWith(10, null), Now, false));
        Assert.Equal("refreshing...", UsageText.ResetText(StateWith(10, Now.AddMinutes(-1)), Now, false));
        Assert.Equal("check PC clock", UsageText.ResetText(StateWith(10, Now.AddHours(9)), Now, false));
    }

    [Fact]
    public void Weekly_text_is_hidden_when_not_reported_and_shows_percent_and_reset_otherwise()
    {
        Assert.Null(UsageText.WeeklyText(null, Now));
        Assert.Equal("11%", UsageText.WeeklyText(new UsageEntry(11.0, null), Now));

        var text = UsageText.WeeklyText(new UsageEntry(18.4, Now.AddHours(53)), Now);
        Assert.StartsWith("18% - resets in 2d 05h (", text);

        Assert.Equal("18% - refreshing...", UsageText.WeeklyText(new UsageEntry(18, Now.AddMinutes(-5)), Now));
    }

    [Theory]
    [InlineData(23, 59, "23h 59m")]
    [InlineData(24, 0, "1d 00h")]
    [InlineData(53, 0, "2d 05h")]
    public void Long_remaining_switches_to_days_from_24_hours(int hours, int minutes, string expected) =>
        Assert.Equal(expected, ClaudeUsageTray.Utilities.TimeFormatter.FormatRemainingLong(new TimeSpan(hours, minutes, 0)));

    [Fact]
    public void Plan_title_falls_back_to_plain_claude()
    {
        Assert.Equal("Claude Pro", UsageText.PlanTitle(StateWith(1, null)));
        Assert.Equal("Claude", UsageText.PlanTitle(UsageState.Initial));
    }
}
