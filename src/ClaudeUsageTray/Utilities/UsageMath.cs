namespace ClaudeUsageTray.Utilities;

public static class UsageMath
{
    /// <summary>Rounded value shown to the user (63.4 becomes 63).</summary>
    public static int DisplayUtilization(double utilization) =>
        (int)Math.Clamp(Math.Round(utilization, MidpointRounding.AwayFromZero), 0, 100);

    /// <summary>Remaining = 100 minus the displayed value, so the two shown numbers always add up to 100.</summary>
    public static int DisplayRemaining(double utilization) => Math.Max(0, 100 - DisplayUtilization(utilization));
}
