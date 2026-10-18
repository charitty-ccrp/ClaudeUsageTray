using System.Text.Json;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Services;

/// <summary>One assistant reply's token usage from a Claude Code transcript, already reduced to a single weight.</summary>
public readonly record struct TranscriptUsage(DateTimeOffset Timestamp, string? MessageId, double Weight);

/// <summary>
/// The real reading an estimate is scaled from: Claude's percentage at <see cref="ReadingAt"/> and how much local
/// Claude Code usage this PC had logged in the same 5-hour window by then.
/// </summary>
public sealed record EstimateBaseline(DateTimeOffset ReadingAt, double Percent, DateTimeOffset WindowStart, DateTimeOffset WindowEnd,
    double WeightAtReading);

/// <summary>
/// Optional "between polls" estimate. Claude's real percentage is the only truth; this only extrapolates it with the
/// tokens Claude Code has logged locally since, so the user sees movement before the next real poll. Pure; no I/O.
/// The estimate is display-only: it must never drive notifications or the tray icon.
/// </summary>
public static class LocalUsageEstimator
{
    public static readonly TimeSpan WindowLength = TimeSpan.FromHours(5);

    // relative cost of each token kind (plain input = 1), roughly in line with API pricing; only ratios matter,
    // because the estimate is the real percentage scaled by how much the weighted total grew
    public const double InputWeight = 1;
    public const double CacheWriteWeight = 1.25;
    public const double CacheReadWeight = 0.1;
    public const double OutputWeight = 5;

    private static ReadOnlySpan<byte> UsageMarker => "\"usage\""u8;

    public static double Weight(long input, long cacheWrite, long cacheRead, long output) =>
        Math.Max(0, input) * InputWeight + Math.Max(0, cacheWrite) * CacheWriteWeight +
        Math.Max(0, cacheRead) * CacheReadWeight + Math.Max(0, output) * OutputWeight;

    /// <summary>Reads one transcript line; null for anything that isn't an assistant reply with usage, or isn't valid JSON.</summary>
    public static TranscriptUsage? ParseLine(ReadOnlySpan<byte> line)
    {
        // most lines are prompts and tool output, often large: skip them without parsing
        if (line.IndexOf(UsageMarker) < 0) return null;
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "assistant") return null;
            if (!root.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp)) return null;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

            string? id = message.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
            var weight = Weight(Count(usage, "input_tokens"), Count(usage, "cache_creation_input_tokens"),
                Count(usage, "cache_read_input_tokens"), Count(usage, "output_tokens"));
            return new TranscriptUsage(timestamp.ToUniversalTime(), id, weight);
        }
        catch (JsonException)
        {
            return null; // a half-written or corrupt line
        }
    }

    public static TranscriptUsage? ParseLine(string line) => ParseLine(System.Text.Encoding.UTF8.GetBytes(line));

    private static long Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;

    /// <summary>The 5-hour window a reading belongs to, from its reset time; null when Claude gave none.</summary>
    public static EstimateBaseline? Baseline(double percent, DateTimeOffset? resetsAt, DateTimeOffset readingAt, Func<DateTimeOffset, DateTimeOffset, double> weightBetween)
    {
        if (resetsAt is not { } end || end <= readingAt) return null;
        var start = end - WindowLength;
        return new EstimateBaseline(readingAt, percent, start, end, weightBetween(start, readingAt));
    }

    /// <summary>
    /// Real percentage scaled by how much local usage grew since the reading. Null when there is nothing to add: no
    /// local usage behind the reading to scale from, no new usage since, or the window has already reset.
    /// </summary>
    public static double? Estimate(EstimateBaseline baseline, double weightNow, DateTimeOffset now)
    {
        if (now >= baseline.WindowEnd || baseline.WeightAtReading <= 0 || weightNow <= baseline.WeightAtReading) return null;
        var estimate = Math.Min(100, baseline.Percent * weightNow / baseline.WeightAtReading);
        return estimate > baseline.Percent ? estimate : null;
    }
}

/// <summary>
/// Weighted token usage from Claude Code's transcripts (<c>~/.claude/projects/**/*.jsonl</c>), read incrementally:
/// each file's byte offset is remembered so a scan only reads what was appended since. Thread-safe.
/// </summary>
public sealed class TranscriptUsageLog(string root)
{
    private const int ChunkSize = 256 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TranscriptUsage> _entries = [];
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>Reads new lines from files written since <paramref name="since"/> and forgets entries older than it.</summary>
    public void Scan(DateTimeOffset since)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Timestamp < since);
            foreach (var old in _seen.Where(p => p.Value < since).Select(p => p.Key).ToList()) _seen.Remove(old);

            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(root)) return;
                files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Could not list transcripts: {ex.GetType().Name}");
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    ReadNew(file, since);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // locked or vanished mid-scan: try again next time from the same offset
                }
            }
        }
    }

    /// <summary>Total weight logged with a timestamp in [from, to].</summary>
    public double WeightBetween(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            double total = 0;
            foreach (var e in _entries)
                if (e.Timestamp >= from && e.Timestamp <= to) total += e.Weight;
            return total;
        }
    }

    private void ReadNew(string file, DateTimeOffset since)
    {
        var info = new FileInfo(file);
        var known = _offsets.TryGetValue(file, out var offset);
        if (!known && info.LastWriteTimeUtc < since.UtcDateTime) return; // untouched since the window opened
        if (info.Length < offset) offset = 0; // rewritten or truncated: start over
        if (info.Length == offset)
        {
            _offsets[file] = offset;
            return;
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[ChunkSize];
        var carry = 0; // bytes of an unfinished line kept at the start of the buffer
        int read;
        while ((read = stream.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            var filled = carry + read;
            var start = 0;
            int newline;
            while ((newline = Array.IndexOf(buffer, (byte)'\n', start, filled - start)) >= 0)
            {
                Add(buffer.AsSpan(start, newline - start), since);
                start = newline + 1;
            }
            offset += start;
            carry = filled - start;
            if (carry > 0) Buffer.BlockCopy(buffer, start, buffer, 0, carry);
            if (carry == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2); // one very long line
        }
        // a trailing line without '\n' may still be being written: it is read again next scan
        _offsets[file] = offset;
    }

    private void Add(ReadOnlySpan<byte> line, DateTimeOffset since)
    {
        if (LocalUsageEstimator.ParseLine(line) is not { } usage || usage.Timestamp < since) return;
        // Claude Code writes one line per content block, each repeating the reply's usage: count every reply once
        if (usage.MessageId is { } id && !_seen.TryAdd(id, usage.Timestamp)) return;
        _entries.Add(usage);
    }
}
