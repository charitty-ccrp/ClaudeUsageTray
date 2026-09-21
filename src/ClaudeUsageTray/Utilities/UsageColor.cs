using System.Drawing;

namespace ClaudeUsageTray.Utilities;

public enum UsageLevel { Unknown, Normal, Warning, High, Critical }

/// <summary>Visual thresholds only; these are not risk levels published by Claude.</summary>
public static class UsageColor
{
    public static UsageLevel Classify(int? displayUtilization) => displayUtilization switch
    {
        null => UsageLevel.Unknown,
        >= 95 => UsageLevel.Critical,
        >= 85 => UsageLevel.High,
        >= 70 => UsageLevel.Warning,
        _ => UsageLevel.Normal,
    };

    public static Color Background(UsageLevel level) => level switch
    {
        UsageLevel.Normal => Color.FromArgb(34, 139, 34),
        UsageLevel.Warning => Color.FromArgb(230, 190, 20),
        UsageLevel.High => Color.FromArgb(230, 120, 20),
        UsageLevel.Critical => Color.FromArgb(200, 30, 30),
        _ => Color.FromArgb(110, 110, 110),
    };

    /// <summary>Bar/accent colour for in-window graphics; brighter on dark backgrounds so it stays readable.</summary>
    public static Color Bar(UsageLevel level, bool dark) => !dark
        ? Background(level)
        : level switch
        {
            UsageLevel.Normal => Color.FromArgb(72, 187, 96),
            UsageLevel.Warning => Color.FromArgb(240, 200, 60),
            UsageLevel.High => Color.FromArgb(245, 150, 60),
            UsageLevel.Critical => Color.FromArgb(240, 90, 80),
            _ => Color.FromArgb(130, 130, 130),
        };

    public static Color Foreground(UsageLevel level) =>
        level == UsageLevel.Warning ? Color.Black : Color.White;
}
