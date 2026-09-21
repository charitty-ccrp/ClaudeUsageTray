using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Tests;

public class UsageMathTests
{
    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(63, 63, 37)]
    [InlineData(63.4, 63, 37)]
    [InlineData(63.5, 64, 36)]
    [InlineData(100, 100, 0)]
    public void Display_values_add_up_to_100(double utilization, int display, int remaining)
    {
        Assert.Equal(display, UsageMath.DisplayUtilization(utilization));
        Assert.Equal(remaining, UsageMath.DisplayRemaining(utilization));
        Assert.Equal(100, display + remaining);
    }
}

public class TimeFormatterTests
{
    [Fact]
    public void Formats_hours_and_minutes() =>
        Assert.Equal("02h 17m", TimeFormatter.FormatRemaining(TimeSpan.FromMinutes(137)));

    [Fact]
    public void Partial_minutes_round_up() =>
        Assert.Equal("00h 01m", TimeFormatter.FormatRemaining(TimeSpan.FromSeconds(5)));

    [Fact]
    public void Zero_or_negative_is_zero() =>
        Assert.Equal("00h 00m", TimeFormatter.FormatRemaining(TimeSpan.FromMinutes(-3)));
}

public class UsageColorTests
{
    [Theory]
    [InlineData(null, UsageLevel.Unknown)]
    [InlineData(0, UsageLevel.Normal)]
    [InlineData(69, UsageLevel.Normal)]
    [InlineData(70, UsageLevel.Warning)]
    [InlineData(84, UsageLevel.Warning)]
    [InlineData(85, UsageLevel.High)]
    [InlineData(94, UsageLevel.High)]
    [InlineData(95, UsageLevel.Critical)]
    [InlineData(100, UsageLevel.Critical)]
    public void Thresholds_use_greater_or_equal(int? percent, UsageLevel expected) =>
        Assert.Equal(expected, UsageColor.Classify(percent));
}

public class BackoffPolicyTests
{
    private static readonly TimeSpan Normal = TimeSpan.FromSeconds(60);

    [Fact]
    public void Success_uses_normal_interval() =>
        Assert.Equal(Normal, BackoffPolicy.NextDelay(UsageErrorKind.None, 0, Normal));

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(4, 120)]
    [InlineData(50, 120)]
    public void Transient_errors_back_off_to_a_120s_cap(int failures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), BackoffPolicy.NextDelay(UsageErrorKind.Network, failures, Normal));
        Assert.Equal(TimeSpan.FromSeconds(seconds), BackoffPolicy.NextDelay(UsageErrorKind.Server, failures, Normal));
    }

    [Theory]
    [InlineData(1, 300)]
    [InlineData(2, 600)]
    [InlineData(9, 900)]
    public void Forbidden_uses_long_backoff(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), BackoffPolicy.NextDelay(UsageErrorKind.Forbidden, failures, Normal));

    [Theory]
    [InlineData(UsageErrorKind.Authentication)]
    [InlineData(UsageErrorKind.NotConfigured)]
    public void Auth_problems_stop_automatic_retry(UsageErrorKind kind) =>
        Assert.Null(BackoffPolicy.NextDelay(kind, 1, Normal));
}

public class LoggerTests
{
    [Fact]
    public void Redacts_session_keys()
    {
        var text = Logger.Redact("failed with sessionKey=sk-ant-sid01-AbC_123-xyz and more");
        Assert.DoesNotContain("AbC_123", text);
        Assert.Contains("[redacted]", text);
    }
}
