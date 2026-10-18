using System.Text;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;

namespace ClaudeUsageTray.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cut-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public class CredentialStoreTests
{
    private const string Key = "sk-ant-sid01-abcdefghijklmnopqrstuvwxyz";

    [Fact]
    public void Round_trips_and_is_not_stored_in_plain_text()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "credential.dat");
        var store = new CredentialStore(path);

        Assert.False(store.Exists);
        store.Save(Key);

        Assert.True(store.Exists);
        Assert.Equal(Key, store.Load());

        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain("sk-ant", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("sk-ant", Encoding.Unicode.GetString(bytes));
    }

    [Fact]
    public void Corrupt_file_loads_as_null_instead_of_throwing()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "credential.dat");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);

        Assert.Null(new CredentialStore(path).Load());
    }

    [Fact]
    public void Delete_removes_the_credential()
    {
        using var dir = new TempDir();
        var store = new CredentialStore(Path.Combine(dir.Path, "credential.dat"));
        store.Save(Key);

        store.Delete();

        Assert.False(store.Exists);
        Assert.Null(store.Load());
    }
}

public class ConfigStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var dir = new TempDir();
        var config = new ConfigStore(Path.Combine(dir.Path, "config.json")).Load();

        Assert.Equal(PollingMode.Balanced, config.PollingMode);
        Assert.False(config.StartWithWindows);
        Assert.Null(config.OrganizationId);
    }

    [Fact]
    public void Round_trips_and_contains_no_credential_field()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        var store = new ConfigStore(path);

        store.Save(new AppConfig
        {
            OrganizationId = "123e4567-e89b-12d3-a456-426614174000",
            PollingMode = PollingMode.Saver,
            QuietHoursEnabled = true,
            QuietStartHour = 23,
            NotifyWarning = false,
        });
        var loaded = store.Load();

        Assert.Equal("123e4567-e89b-12d3-a456-426614174000", loaded.OrganizationId);
        Assert.Equal(PollingMode.Saver, loaded.PollingMode);
        Assert.True(loaded.QuietHoursEnabled);
        Assert.Equal(23, loaded.QuietStartHour);
        Assert.False(loaded.NotifyWarning);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("sessionKey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-ant", text);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(path, "{ definitely not json");

        Assert.Equal(PollingMode.Balanced, new ConfigStore(path).Load().PollingMode);
    }

    [Fact]
    public void Old_fixed_refresh_interval_is_ignored_and_becomes_balanced_polling()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(path, """{ "refreshIntervalSeconds": 30, "notificationsEnabled": false }""");

        var config = new ConfigStore(path).Load();

        Assert.Equal(PollingMode.Balanced, config.PollingMode);
        Assert.False(config.NotificationsEnabled);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(40, 23)]
    public void Quiet_hours_are_clamped_to_a_real_hour(int value, int expected)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(path, $$"""{ "quietStartHour": {{value}}, "quietEndHour": {{value}} }""");

        var config = new ConfigStore(path).Load();

        Assert.Equal(expected, config.QuietStartHour);
        Assert.Equal(expected, config.QuietEndHour);
    }
}

public class StartupManagerTests
{
    [Fact]
    public void Creates_and_removes_the_startup_shortcut()
    {
        using var dir = new TempDir();
        var manager = new StartupManager(dir.Path, targetExe: Environment.ProcessPath);

        Assert.False(manager.IsEnabled);
        Assert.True(manager.SetEnabled(true));
        Assert.True(manager.IsEnabled);
        Assert.True(File.Exists(Path.Combine(dir.Path, "ClaudeUsageTray.lnk")));

        Assert.True(manager.SetEnabled(false));
        Assert.False(manager.IsEnabled);
    }

    [Fact]
    public void Start_menu_and_desktop_shortcuts_are_created_and_removed_together()
    {
        using var startMenu = new TempDir();
        using var desktop = new TempDir();
        var shortcuts = new AppShortcuts(startMenu.Path, desktop.Path, Environment.ProcessPath);

        Assert.False(shortcuts.Exist);
        Assert.True(shortcuts.SetEnabled(true));
        Assert.True(shortcuts.Exist);
        Assert.True(File.Exists(Path.Combine(startMenu.Path, "Claude Usage Tray.lnk")));
        Assert.True(File.Exists(Path.Combine(desktop.Path, "Claude Usage Tray.lnk")));

        Assert.True(shortcuts.SetEnabled(false));
        Assert.False(shortcuts.Exist);
        Assert.Empty(Directory.GetFiles(desktop.Path));
    }
}
