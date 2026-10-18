using System.Text;
using ClaudeUsageTray.Services;

namespace ClaudeUsageTray.Tests;

public class LocalEstimateTests
{
    // real time (whole seconds), because the log skips files whose last write is older than the window
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static string Line(DateTimeOffset at, string id, int input = 0, int output = 0, int cacheWrite = 0, int cacheRead = 0) =>
        $"{{\"type\":\"assistant\",\"timestamp\":\"{at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)}\",\"message\":{{\"id\":\"{id}\"," +
        $"\"usage\":{{\"input_tokens\":{input},\"cache_creation_input_tokens\":{cacheWrite},\"cache_read_input_tokens\":{cacheRead}," +
        $"\"output_tokens\":{output},\"service_tier\":\"standard\"}}}}}}";

    private static EstimateBaseline Baseline(double percent, double weightAtReading) =>
        new(Now, percent, Now - TimeSpan.FromHours(2), Now + TimeSpan.FromHours(3), weightAtReading);

    [Fact]
    public void Parses_an_assistant_line_into_a_weighted_total()
    {
        var usage = LocalUsageEstimator.ParseLine(Line(Now, "msg_1", input: 100, output: 10, cacheWrite: 40, cacheRead: 1000));

        Assert.NotNull(usage);
        Assert.Equal(Now, usage.Value.Timestamp);
        Assert.Equal("msg_1", usage.Value.MessageId);
        Assert.Equal(LocalUsageEstimator.Weight(100, 40, 1000, 10), usage.Value.Weight);
        Assert.Equal(100 + 40 * 1.25 + 1000 * 0.1 + 10 * 5, usage.Value.Weight);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json \"usage\"")]
    [InlineData("{\"type\":\"assistant\",\"timestamp\":\"2026-10-18T12:00:00Z\",\"message\":{\"usage\":{\"input_tok")]
    [InlineData("{\"type\":\"user\",\"timestamp\":\"2026-10-18T12:00:00Z\",\"message\":{\"usage\":{\"input_tokens\":5}}}")]
    [InlineData("{\"type\":\"assistant\",\"timestamp\":\"yesterday\",\"message\":{\"usage\":{\"input_tokens\":5}}}")]
    [InlineData("{\"type\":\"assistant\",\"timestamp\":\"2026-10-18T12:00:00Z\",\"message\":{\"usage\":\"none\"}}")]
    [InlineData("[\"usage\"]")]
    public void Malformed_or_irrelevant_lines_are_ignored(string line) => Assert.Null(LocalUsageEstimator.ParseLine(line));

    [Fact]
    public void Odd_field_types_count_as_zero_rather_than_failing()
    {
        var usage = LocalUsageEstimator.ParseLine(
            "{\"type\":\"assistant\",\"timestamp\":\"2026-10-18T12:00:00Z\",\"message\":{\"usage\":{\"input_tokens\":\"7\",\"output_tokens\":2}}}");
        Assert.Equal(10, usage!.Value.Weight);
    }

    [Fact]
    public void Estimate_scales_the_real_reading_by_local_growth()
    {
        Assert.Equal(60, LocalUsageEstimator.Estimate(Baseline(40, 1000), 1500, Now.AddMinutes(5))!.Value, 6);
    }

    [Fact]
    public void Estimate_never_exceeds_one_hundred_percent() =>
        Assert.Equal(100, LocalUsageEstimator.Estimate(Baseline(80, 100), 1000, Now.AddMinutes(5)));

    [Fact]
    public void No_estimate_without_new_usage_or_without_a_local_baseline()
    {
        Assert.Null(LocalUsageEstimator.Estimate(Baseline(40, 1000), 1000, Now.AddMinutes(5)));
        Assert.Null(LocalUsageEstimator.Estimate(Baseline(40, 0), 500, Now.AddMinutes(5))); // all usage came from elsewhere
        Assert.Null(LocalUsageEstimator.Estimate(Baseline(0, 1000), 5000, Now.AddMinutes(5)));
    }

    [Fact]
    public void No_estimate_once_the_window_has_reset() =>
        Assert.Null(LocalUsageEstimator.Estimate(Baseline(40, 1000), 2000, Now + TimeSpan.FromHours(3)));

    [Fact]
    public void Baseline_uses_the_five_hour_window_ending_at_the_reset()
    {
        var reset = Now + TimeSpan.FromHours(1);
        DateTimeOffset? from = null, to = null;
        var baseline = LocalUsageEstimator.Baseline(30, reset, Now, (f, t) => { from = f; to = t; return 42; });

        Assert.NotNull(baseline);
        Assert.Equal(reset - TimeSpan.FromHours(5), from);
        Assert.Equal(Now, to);
        Assert.Equal(42, baseline.WeightAtReading);
        Assert.Null(LocalUsageEstimator.Baseline(30, null, Now, (_, _) => 1));
        Assert.Null(LocalUsageEstimator.Baseline(30, Now.AddMinutes(-1), Now, (_, _) => 1));
    }

    [Fact]
    public void Log_reads_files_incrementally_dedupes_replies_and_tolerates_bad_lines()
    {
        var dir = Directory.CreateTempSubdirectory("cut-transcripts-").FullName;
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(dir, "C--proj")).FullName;
            var file = Path.Combine(project, "session.jsonl");
            var since = Now - TimeSpan.FromHours(5);

            File.WriteAllText(file, string.Join("\n",
                Line(Now.AddMinutes(-10), "msg_a", input: 100),
                Line(Now.AddMinutes(-10), "msg_a", input: 100), // same reply, second content block
                "{ broken",
                "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}",
                Line(Now - TimeSpan.FromHours(6), "msg_old", input: 999), // before the window
                Line(Now.AddMinutes(-5), "msg_b", output: 10)) + "\n");

            var log = new TranscriptUsageLog(dir);
            log.Scan(since);
            Assert.Equal(150, log.WeightBetween(since, Now));

            // an unfinished last line is not counted until its newline arrives
            var partial = Line(Now.AddMinutes(-1), "msg_c", input: 7);
            File.AppendAllText(file, partial[..20]);
            log.Scan(since);
            Assert.Equal(150, log.WeightBetween(since, Now));

            File.AppendAllText(file, partial[20..] + "\n");
            log.Scan(since);
            Assert.Equal(157, log.WeightBetween(since, Now));
            Assert.Equal(150, log.WeightBetween(since, Now.AddMinutes(-2)));

            // nothing appended: scanning again changes nothing
            log.Scan(since);
            Assert.Equal(157, log.WeightBetween(since, Now));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Log_starts_over_when_a_file_is_rewritten_and_handles_very_long_lines()
    {
        var dir = Directory.CreateTempSubdirectory("cut-transcripts-").FullName;
        try
        {
            var file = Path.Combine(dir, "s.jsonl");
            var since = Now - TimeSpan.FromHours(5);
            var huge = "{\"type\":\"user\",\"message\":{\"content\":\"" + new string('x', 600_000) + "\"}}";
            File.WriteAllText(file, huge + "\n" + Line(Now.AddMinutes(-3), "msg_a", input: 10) + "\n" + Line(Now.AddMinutes(-2), "msg_b", input: 20) + "\n",
                new UTF8Encoding(false));

            var log = new TranscriptUsageLog(dir);
            log.Scan(since);
            Assert.Equal(30, log.WeightBetween(since, Now));

            File.WriteAllText(file, Line(Now.AddMinutes(-1), "msg_c", input: 5) + "\n");
            log.Scan(since);
            Assert.Equal(35, log.WeightBetween(since, Now));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Log_survives_a_missing_folder()
    {
        var log = new TranscriptUsageLog(Path.Combine(Path.GetTempPath(), "cut-missing-" + Guid.NewGuid()));
        log.Scan(Now.AddHours(-5));
        Assert.Equal(0, log.WeightBetween(Now.AddHours(-5), Now));
    }
}
