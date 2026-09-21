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

        Assert.Equal(60, config.RefreshIntervalSeconds);
        Assert.False(config.StartWithWindows);
        Assert.Null(config.OrganizationId);
    }

    [Fact]
    public void Round_trips_and_contains_no_credential_field()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        var store = new ConfigStore(path);

        store.Save(new AppConfig { OrganizationId = "123e4567-e89b-12d3-a456-426614174000", RefreshIntervalSeconds = 90 });
        var loaded = store.Load();

        Assert.Equal("123e4567-e89b-12d3-a456-426614174000", loaded.OrganizationId);
        Assert.Equal(90, loaded.RefreshIntervalSeconds);
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

        Assert.Equal(60, new ConfigStore(path).Load().RefreshIntervalSeconds);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(999999, 3600)]
    public void Refresh_interval_is_clamped(int value, int expected)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(path, $$"""{ "refreshIntervalSeconds": {{value}} }""");

        Assert.Equal(expected, new ConfigStore(path).Load().RefreshIntervalSeconds);
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
}
