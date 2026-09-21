namespace ClaudeUsageTray.App;

internal static class AppConstants
{
    public const string AppName = "ClaudeUsageTray";
    public const string MutexName = @"Local\ClaudeUsageTray";
    public const string ClaudeHost = "claude.ai";
    public const string ClaudeHomeUrl = "https://claude.ai/";
    public const int DefaultRefreshSeconds = 60;
    public const int MinRefreshSeconds = 30;
    public const int MaxRefreshSeconds = 3600;
    public const int MinOverlayOpacity = 30;
    public const int MaxOverlayOpacity = 100;
    public const int DefaultOverlayOpacity = 90;

    /// <summary>Beyond this time-to-reset the local clock is probably wrong (5h window plus slack).</summary>
    public static readonly TimeSpan MaxPlausibleReset = TimeSpan.FromHours(5) + TimeSpan.FromMinutes(10);

    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);
}
