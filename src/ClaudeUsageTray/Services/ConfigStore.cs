using System.Text.Json;
using ClaudeUsageTray.App;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Services;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public ConfigStore(string? path = null) => _path = path ?? AppPaths.ConfigFile;

    /// <summary>Missing or corrupt files fall back to defaults so a bad config never blocks startup.</summary>
    public AppConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), Options) ?? new AppConfig();
                config.Normalize();
                return config;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Config could not be read, using defaults: {ex.GetType().Name}");
        }
        return new AppConfig();
    }

    public void Save(AppConfig config)
    {
        config.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
