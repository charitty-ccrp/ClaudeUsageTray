using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.UI;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Tests;

public class BurnLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Collecting_message_until_there_is_a_projection()
    {
        var (text, warn) = UsageText.BurnLine(new UsageEntry(30, Now.AddHours(3)), Now, null, stale: false);

        Assert.Equal("Collecting usage rate...", text);
        Assert.False(warn);
    }

    [Fact]
    public void Steady_usage_is_reassuring()
    {
        var (text, warn) = UsageText.BurnLine(new UsageEntry(30, Now.AddHours(3)), Now, new BurnProjection(0.2, null), false);

        Assert.Contains("steady", text);
        Assert.False(warn);
    }

    [Fact]
    public void Warns_when_the_limit_would_be_hit_before_reset()
    {
        var entry = new UsageEntry(60, Now.AddHours(3));
        var (text, warn) = UsageText.BurnLine(entry, Now, new BurnProjection(20, Now.AddHours(2)), false);

        Assert.True(warn);
        Assert.StartsWith("~20%/h - could run out by ", text);
        Assert.EndsWith("1h 00m before reset", text);
    }

    [Fact]
    public void No_warning_when_the_window_resets_first()
    {
        var entry = new UsageEntry(30, Now.AddHours(1));
        var (text, warn) = UsageText.BurnLine(entry, Now, new BurnProjection(9, Now.AddHours(5)), false);

        Assert.False(warn);
        Assert.Equal("~9%/h - resets before you run out", text);
    }

    [Fact]
    public void Hidden_for_stale_data_and_when_the_limit_is_already_reached()
    {
        Assert.Null(UsageText.BurnLine(new UsageEntry(30, Now.AddHours(1)), Now, new BurnProjection(9, Now.AddHours(2)), stale: true).Text);
        Assert.Null(UsageText.BurnLine(new UsageEntry(100, Now.AddHours(1)), Now, new BurnProjection(9, Now), false).Text);
    }
}

public class TooltipAndToastTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 2, 0, 0, TimeSpan.Zero);

    private static UsageState State(double fiveHour, double? week = null, double? opus = null, bool stale = false)
    {
        var response = new UsageResponse(
            new UsageEntry(fiveHour, Now.AddMinutes(195)),
            week is null ? null : new UsageEntry(week.Value, Now.AddHours(53)),
            opus is null ? null : new UsageEntry(opus.Value, Now.AddHours(53)),
            null);
        return new UsageState(new UsageSnapshot(response, Now), stale, stale ? UsageErrorKind.Network : UsageErrorKind.None, null, Now, null);
    }

    [Fact]
    public void Tooltip_labels_the_local_estimate_and_keeps_the_real_figure_first()
    {
        var text = UsageText.Tooltip(State(60, week: 18), Now, estimatePercent: 66);

        Assert.StartsWith("Claude 60% 5h · 18% wk\n≈ 66% (est.) · Reset: 03h 15m\n", text);
        Assert.DoesNotContain("est.", UsageText.Tooltip(State(60), Now, estimatePercent: 60));
        Assert.DoesNotContain("est.", UsageText.Tooltip(State(60), Now));
    }

    [Fact]
    public void Tooltip_lists_both_windows_and_the_reset_of_the_one_driving_the_icon()
    {
        var text = UsageText.Tooltip(State(60, week: 18), Now);

        Assert.StartsWith("Claude 60% 5h · 18% wk\nReset: 03h 15m\nUpdated: ", text);
    }

    [Fact]
    public void Tooltip_reset_line_is_always_the_five_hour_one_even_when_weekly_is_higher()
    {
        var text = UsageText.Tooltip(State(20, week: 92), Now);

        Assert.StartsWith("Claude 20% 5h · 92% wk\nReset: 03h 15m\n", text);
    }

    [Fact]
    public void Tooltip_mentions_only_five_hour_and_weekly_never_model_windows()
    {
        Assert.DoesNotContain("opus", UsageText.Tooltip(State(60, week: 18, opus: 97), Now));
        Assert.Equal("Claude 60%", UsageText.Tooltip(State(60), Now).Split('\n')[0]);
    }

    [Fact]
    public void Tooltip_never_exceeds_the_notify_icon_limit_even_with_every_window_and_stale()
    {
        var text = UsageText.Tooltip(State(100, week: 100, opus: 100, stale: true), Now);

        Assert.True(text.Length <= 127, text);
        Assert.Contains("(last known)", text);
        Assert.Contains("Last update", text);
    }

    [Fact]
    public void Toast_for_a_rising_level_names_the_window_and_when_it_resets()
    {
        var toast = UsageText.Toast(
            [new UsageNotification("five_hour", "5-HOUR SESSION", 86, UsageLevel.High, false, Now.AddMinutes(80))], Now);

        Assert.Equal("Claude 5-hour usage at 86%", toast.Title);
        Assert.Contains("Running low", toast.Text);
        Assert.Contains("Resets in 01h 20m", toast.Text);
        Assert.True(toast.Warn);
    }

    [Fact]
    public void Toast_for_a_reset_is_informational()
    {
        var toast = UsageText.Toast(
            [new UsageNotification("seven_day", "WEEKLY", 1, UsageLevel.Normal, true, Now.AddDays(7))], Now);

        Assert.Equal("Claude Weekly limit reset", toast.Title);
        Assert.Equal("Usage is back to 1%.", toast.Text);
        Assert.False(toast.Warn);
    }

    [Fact]
    public void Several_alerts_are_combined_into_one_toast()
    {
        var toast = UsageText.Toast(
        [
            new UsageNotification("five_hour", "5-HOUR SESSION", 72, UsageLevel.Warning, false, null),
            new UsageNotification("seven_day", "WEEKLY", 88, UsageLevel.High, false, null),
        ], Now);

        Assert.Equal("Claude usage alert", toast.Title);
        Assert.Equal(2, toast.Text.Split('\n').Length);
    }
}

public class PanelModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 45, 16, TimeSpan.Zero);

    private static UsageState Ok(double fiveHour, double? week = null, double? opus = null) => new(
        new UsageSnapshot(new UsageResponse(
            new UsageEntry(fiveHour, Now.AddMinutes(195)),
            week is null ? null : new UsageEntry(week.Value, Now.AddHours(53)),
            opus is null ? null : new UsageEntry(opus.Value, Now.AddHours(53)),
            null), Now, "Pro"),
        false, UsageErrorKind.None, null, Now, Now.AddSeconds(42));

    private static PanelModel Build(UsageState state, bool refreshing = false, int? credentialAge = null, DateTimeOffset? at = null) =>
        PanelModelBuilder.Build(state, at ?? Now, new BurnRateTracker(), "Claude Code", credentialAge, refreshing);

    [Fact]
    public void Estimate_line_is_labelled_and_never_replaces_the_real_percentage()
    {
        var model = PanelModelBuilder.Build(Ok(40), Now, new BurnRateTracker(), "Claude Code", null, false, estimatePercent: 47);

        Assert.Equal(40, model.Primary!.Percent);
        Assert.StartsWith("≈ 47% (est.)", model.EstimateLine);
        Assert.NotEqual(Build(Ok(40)).Signature, model.Signature);
        Assert.Null(Build(Ok(40)).EstimateLine);
        Assert.Null(PanelModelBuilder.Build(Ok(40), Now, new BurnRateTracker(), "Claude Code", null, false, estimatePercent: 40).EstimateLine);
    }

    [Fact]
    public void Splits_the_five_hour_window_from_the_others()
    {
        var model = Build(Ok(40, week: 92, opus: 5));

        Assert.Equal(40, model.Primary!.Percent);
        Assert.Equal(["seven_day", "seven_day_opus"], model.Others.Select(o => o.Key));
        Assert.Equal([92, 5], model.Others.Select(o => o.Percent));
    }

    [Fact]
    public void Without_weekly_data_there_are_no_secondary_rows()
    {
        Assert.Empty(Build(Ok(80)).Others);
    }

    [Fact]
    public void Header_and_details_use_plan_source_and_reset_text()
    {
        var model = Build(Ok(60, week: 18));

        Assert.Equal("Claude Pro", model.Title);
        Assert.Equal("Claude Code", model.Badge);
        Assert.Equal("Resets in 03h 15m (at " + Now.AddMinutes(195).ToLocalTime().ToString("HH:mm") + ")", model.Primary!.Detail);
        Assert.StartsWith("Resets in 2d 05h (", model.Others.Single().Detail);
        Assert.Equal("Collecting usage rate...", model.BurnLine);
    }

    [Fact]
    public void Footer_shows_update_time_and_the_countdown_to_the_next_refresh()
    {
        var footer = PanelModelBuilder.Footer(Ok(10), Now, refreshing: false);

        Assert.StartsWith("Updated ", footer);
        Assert.EndsWith("next in 42s", footer);
    }

    [Fact]
    public void Footer_says_refreshing_while_a_request_is_in_flight()
    {
        Assert.Equal("Refreshing...", PanelModelBuilder.Footer(Ok(10), Now, refreshing: true));
    }

    [Fact]
    public void Footer_for_failures_says_stale_and_retry_or_waiting_for_the_user()
    {
        var stale = Ok(10) with { IsStale = true, Error = UsageErrorKind.Network, NextAttemptAt = Now.AddSeconds(15) };
        var footer = PanelModelBuilder.Footer(stale, Now, false);
        Assert.Contains("(last known value)", footer);
        Assert.EndsWith("retry in 15s", footer);

        var auth = Ok(10) with { IsStale = true, Error = UsageErrorKind.Authentication, NextAttemptAt = null };
        Assert.EndsWith("waiting for you", PanelModelBuilder.Footer(auth, Now, false));

        Assert.StartsWith("No data yet", PanelModelBuilder.Footer(UsageState.Initial, Now, false));
    }

    [Fact]
    public void Status_prefers_the_specific_error_message_and_keeps_the_last_good_value()
    {
        var state = Ok(63) with { IsStale = true, Error = UsageErrorKind.Forbidden, ErrorMessage = "Blocked by a Cloudflare challenge (HTTP 403)." };

        var (text, tone) = PanelModelBuilder.Status(state, null);

        Assert.Equal(StatusTone.Error, tone);
        Assert.Contains("Cloudflare", text);
        Assert.EndsWith("Last good value: 63%", text);
    }

    [Fact]
    public void Status_covers_waiting_credential_age_and_the_healthy_case()
    {
        Assert.Equal(StatusTone.Info, PanelModelBuilder.Status(UsageState.Initial, null).Tone);
        Assert.Equal(StatusTone.Warning, PanelModelBuilder.Status(Ok(10), 27).Tone);
        Assert.Equal(StatusTone.None, PanelModelBuilder.Status(Ok(10), 3).Tone);
    }

    [Fact]
    public void Signature_is_stable_for_identical_content_and_changes_when_anything_visible_changes()
    {
        var a = Build(Ok(60, week: 18));
        var b = Build(Ok(60, week: 18));
        Assert.Equal(a.Signature, b.Signature);

        Assert.NotEqual(a.Signature, Build(Ok(61, week: 18)).Signature);
        Assert.NotEqual(a.Signature, Build(Ok(60, week: 19)).Signature);
        Assert.NotEqual(a.Signature, Build(Ok(60, week: 18), refreshing: true).Signature);
        Assert.NotEqual(a.Signature, Build(Ok(60, week: 18), at: Now.AddSeconds(5)).Signature); // footer countdown moved
    }

    [Fact]
    public void Summary_reads_naturally_for_screen_readers()
    {
        var summary = Build(Ok(60, week: 18)).Summary;

        Assert.Contains("Claude Pro", summary);
        Assert.Contains("5-hour session 60 percent", summary);
        Assert.Contains("weekly 18 percent", summary);
    }
}
