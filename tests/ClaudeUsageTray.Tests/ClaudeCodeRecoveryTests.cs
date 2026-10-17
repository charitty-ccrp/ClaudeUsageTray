using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;

namespace ClaudeUsageTray.Tests;

public class AutoLaunchConfigTests
{
    [Fact]
    public void Is_on_by_default_and_round_trips()
    {
        Assert.True(new AppConfig().AutoLaunchClaudeCode);

        using var dir = new TempDir();
        var store = new ConfigStore(Path.Combine(dir.Path, "config.json"));
        store.Save(new AppConfig { AutoLaunchClaudeCode = false });

        Assert.False(store.Load().AutoLaunchClaudeCode);
    }

    [Fact]
    public void A_config_file_from_before_this_setting_existed_still_loads_with_the_default()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(path, """{ "authMode": "ClaudeCodeOAuth" }""");

        Assert.True(new ConfigStore(path).Load().AutoLaunchClaudeCode);
    }
}

public class ClaudeCodeRecoveryPolicyTests
{
    private static bool Should(
        AuthMode mode = AuthMode.ClaudeCodeOAuth,
        UsageErrorKind error = UsageErrorKind.CredentialUnavailable,
        bool enabled = true,
        bool alreadyAttempted = false,
        bool processRunning = false) =>
        ClaudeCodeRecoveryPolicy.ShouldAutoLaunch(mode, error, enabled, alreadyAttempted, processRunning);

    [Fact]
    public void Launches_when_the_oauth_login_has_expired_and_nothing_else_is_in_the_way() =>
        Assert.True(Should());

    [Fact]
    public void Never_launches_for_the_cookie_mode() =>
        Assert.False(Should(mode: AuthMode.WebCookie));

    [Theory]
    [InlineData(UsageErrorKind.Authentication)]
    [InlineData(UsageErrorKind.Forbidden)]
    [InlineData(UsageErrorKind.Network)]
    [InlineData(UsageErrorKind.Server)]
    [InlineData(UsageErrorKind.None)]
    public void Only_reacts_to_credential_unavailable_not_other_errors(UsageErrorKind error) =>
        Assert.False(Should(error: error));

    [Fact]
    public void Respects_the_setting_being_turned_off() =>
        Assert.False(Should(enabled: false));

    [Fact]
    public void Does_not_reopen_a_window_it_already_tried_for_this_failure_episode() =>
        Assert.False(Should(alreadyAttempted: true));

    [Fact]
    public void Does_not_open_a_second_window_while_one_is_already_running() =>
        Assert.False(Should(processRunning: true));
}

public class ClaudeCodeLauncherTests
{
    [Fact]
    public void Reports_a_helpful_error_when_claude_is_not_installed()
    {
        using var launcher = new ClaudeCodeLauncher(resolve: () => null);

        var (started, error) = launcher.Launch();

        Assert.False(started);
        Assert.False(launcher.IsRunning);
        Assert.False(launcher.IsAvailable);
        Assert.Equal(ClaudeCodeLauncher.NotInstalledMessage, error);
    }

    [Fact]
    public void Starts_a_process_reports_it_as_running_and_never_starts_a_second_one_until_it_exits()
    {
        // "ping" against localhost runs for a short, predictable time and always exists on Windows, so this needs no fixture on disk.
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        using var launcher = new ClaudeCodeLauncher(arguments: "-n 3 127.0.0.1", workingDirectory: Path.GetTempPath(), resolve: () => ping);

        var (started, error) = launcher.Launch();
        Assert.True(started);
        Assert.Null(error);
        Assert.True(launcher.IsRunning);

        var (startedAgain, secondError) = launcher.Launch();
        Assert.False(startedAgain);
        Assert.Equal("Claude Code is already open.", secondError);

        var waited = SpinWait.SpinUntil(() => !launcher.IsRunning, TimeSpan.FromSeconds(10));
        Assert.True(waited, "ping should have finished within 10 seconds");
        Assert.True(launcher.Launch().Started, "a new launch must succeed once the previous process has exited");
    }

    [Fact]
    public void Resolve_prefers_PATH_then_falls_back_to_the_usual_install_folders()
    {
        var onPath = Path.Combine(@"C:\tools", "claude.exe");
        var native = Path.Combine(@"C:\Users\me", ".local", "bin", "claude.exe");
        var npm = Path.Combine(@"C:\Users\me\AppData\Roaming", "npm", "claude.cmd");

        Assert.Equal(onPath, ClaudeCodeLauncher.Resolve(@"C:\tools;relative\dir", @"C:\Users\me", @"C:\Users\me\AppData\Roaming",
            p => p == onPath || p == native));
        Assert.Equal(native, ClaudeCodeLauncher.Resolve(@"C:\tools", @"C:\Users\me", @"C:\Users\me\AppData\Roaming", p => p == native));
        Assert.Equal(npm, ClaudeCodeLauncher.Resolve(null, @"C:\Users\me", @"C:\Users\me\AppData\Roaming", p => p == npm));
        Assert.Null(ClaudeCodeLauncher.Resolve(@"C:\tools", @"C:\Users\me", @"C:\Users\me\AppData\Roaming", _ => false));
    }

    [Fact]
    public void Resolve_ignores_relative_PATH_entries()
    {
        Assert.Null(ClaudeCodeLauncher.Resolve(@".;bin", @"C:\Users\me", @"C:\x", p => !Path.IsPathFullyQualified(p)));
    }
}
