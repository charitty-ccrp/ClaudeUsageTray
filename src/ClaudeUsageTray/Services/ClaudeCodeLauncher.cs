using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeUsageTray.Services;

/// <summary>
/// Opens the Claude Code CLI in a normal, visible console window - the same thing the user would do by hand
/// (open a terminal, run a "claude" command). We never automate past that: any prompt the CLI shows is answered
/// by the user. TrayApplicationContext uses two of these: one running "claude update" for the automatic, silent
/// recovery path (a maintenance subcommand that refreshes the token and then exits on its own, so the window
/// closes itself), and one running plain "claude" for the manual "Sign in..." menu item, which opens the full
/// interactive session in case the refresh token itself is gone and the user needs to log in again.
/// </summary>
public sealed class ClaudeCodeLauncher(string? arguments = null, string? workingDirectory = null, Func<string?>? resolve = null) : IDisposable
{
    private readonly string _workingDirectory = workingDirectory ??
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private readonly Func<string?> _resolve = resolve ?? FindClaude;

    private Process? _process;

    /// <summary>True while the window we opened is still running (we never start a second one on top of it).</summary>
    public bool IsRunning
    {
        get
        {
            if (_process is null) return false;
            try
            {
                if (!_process.HasExited) return true;
            }
            catch (InvalidOperationException)
            {
                // process handle no longer valid; treat as not running
            }
            _process.Dispose();
            _process = null;
            return false;
        }
    }

    /// <summary>Whether the Claude Code CLI can be found on this PC (PATH or its usual install folders).</summary>
    public bool IsAvailable => _resolve() is not null;

    public const string NotInstalledMessage =
        "The Claude Code command-line tool (claude) was not found on this PC, so the login cannot be refreshed " +
        "automatically. Open Claude Code (or the Claude app) once and the usage will reconnect by itself.";

    /// <summary>Starts the CLI in its own console window. Returns an error message on failure instead of throwing.</summary>
    public (bool Started, string? Error) Launch()
    {
        if (IsRunning) return (false, "Claude Code is already open.");

        // a full path, never a bare name: avoids running whatever "claude" happens to sit in the current folder
        if (_resolve() is not { } path) return (false, NotInstalledMessage);

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true, // opens a real console window, exactly like the user typing the command themselves
                WorkingDirectory = _workingDirectory,
            };
            if (!string.IsNullOrEmpty(arguments)) info.Arguments = arguments;
            var process = Process.Start(info);
            if (process is null) return (false, "Could not start Claude Code.");
            _process = process;
            return (true, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return (false, $"Could not start '{path}'.");
        }
    }

    public static string? FindClaude() => Resolve(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        File.Exists);

    /// <summary>PATH first (as a terminal would), then the native installer's and npm's default folders.</summary>
    public static string? Resolve(string? pathVariable, string userProfile, string appData, Func<string, bool> exists)
    {
        string[] names = ["claude.exe", "claude.cmd"];
        var folders = (pathVariable ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified)
            .Append(Path.Combine(userProfile, ".local", "bin"))
            .Append(Path.Combine(appData, "npm"));

        foreach (var folder in folders)
            foreach (var name in names)
            {
                var candidate = Path.Combine(folder, name);
                if (exists(candidate)) return candidate;
            }
        return null;
    }

    public void Dispose() => _process?.Dispose();
}
