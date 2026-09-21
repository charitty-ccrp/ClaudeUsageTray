using System.Text.RegularExpressions;
using ClaudeUsageTray.App;

namespace ClaudeUsageTray.Utilities;

/// <summary>Tiny file logger. Never throws; redacts anything that looks like a Claude session key.</summary>
public static partial class Logger
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    /// <summary>Lets tests redirect logs so they never touch the real log folder.</summary>
    internal static string? DirectoryOverride { get; set; }

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-\.%=]+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"Bearer\s+[^\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    public static string Redact(string text) => BearerPattern().Replace(KeyPattern().Replace(text, "[redacted]"), "Bearer [redacted]");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {Redact(message)}{Environment.NewLine}";
            lock (Gate)
            {
                var dir = DirectoryOverride ?? AppPaths.LogsDir;
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "app.log");
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    File.Move(file, Path.Combine(dir, "app.1.log"), overwrite: true);
                }
                File.AppendAllText(file, line);
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
