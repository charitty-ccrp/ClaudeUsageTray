using Microsoft.Win32;

namespace ClaudeUsageTray.UI;

/// <summary>
/// Colours for the parts of the popup we paint ourselves. Standard controls follow Windows through
/// Application.SetColorMode; this keeps the custom drawing in step with the same light/dark setting.
/// </summary>
internal static class Theme
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; } = ReadIsDark();

    /// <summary>Re-read the Windows setting (call after a UserPreferenceChanged event).</summary>
    public static void Refresh() => IsDark = ReadIsDark();

    public static Color Background => IsDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(249, 249, 249);
    public static Color Foreground => IsDark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(28, 28, 28);
    public static Color Muted => IsDark ? Color.FromArgb(165, 165, 165) : Color.FromArgb(105, 105, 105);
    public static Color Divider => IsDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(224, 224, 224);
    public static Color Track => IsDark ? Color.FromArgb(66, 66, 66) : Color.FromArgb(226, 226, 226);
    public static Color Border => IsDark ? Color.FromArgb(75, 75, 75) : Color.FromArgb(200, 200, 200);
    public static Color Error => IsDark ? Color.FromArgb(255, 125, 115) : Color.FromArgb(192, 40, 40);
    public static Color Warning => IsDark ? Color.FromArgb(255, 195, 85) : Color.FromArgb(170, 105, 0);

    private static bool ReadIsDark()
    {
        try
        {
            return Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
