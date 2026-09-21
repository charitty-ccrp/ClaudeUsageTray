namespace ClaudeUsageTray.UI;

/// <summary>
/// Decides where the overlay goes. Pure so it can be tested: a saved position is honoured only while enough of the
/// window is still reachable, otherwise the overlay falls back to the primary screen's top-right corner. This
/// prevents the classic "dragged it to a monitor that was later unplugged" invisible window.
/// </summary>
internal static class OverlayPlacement
{
    public const int Margin = 16;
    public const int MinVisibleWidth = 80;
    public const int MinVisibleHeight = 40;
    private const long MaxCoordinate = 100_000;

    /// <param name="workingAreas">Working areas of all screens; the first one is the primary screen.</param>
    public static Point Resolve(int? savedX, int? savedY, Size size, IReadOnlyList<Rectangle> workingAreas)
    {
        if (workingAreas.Count == 0) return new Point(Margin, Margin);
        var fallback = DefaultPosition(size, workingAreas[0]);

        if (savedX is not { } x || savedY is not { } y) return fallback;
        if (Math.Abs((long)x) > MaxCoordinate || Math.Abs((long)y) > MaxCoordinate) return fallback; // corrupt value; also avoids Rectangle overflow

        var bounds = new Rectangle(x, y, size.Width, size.Height);
        var host = BestScreen(bounds, workingAreas);
        return host is null ? fallback : Nudge(bounds, host.Value);
    }

    public static Point DefaultPosition(Size size, Rectangle primaryWorkingArea) =>
        new(primaryWorkingArea.Right - size.Width - Margin, primaryWorkingArea.Top + Margin);

    /// <summary>The screen the window overlaps most; null when it is entirely off every screen (then we reset to the default).</summary>
    private static Rectangle? BestScreen(Rectangle bounds, IReadOnlyList<Rectangle> areas)
    {
        Rectangle? best = null;
        long bestArea = 0;
        foreach (var area in areas)
        {
            var overlap = Rectangle.Intersect(bounds, area);
            if (overlap.Width <= 0 || overlap.Height <= 0) continue;

            var overlapArea = (long)overlap.Width * overlap.Height;
            if (overlapArea > bestArea)
            {
                best = area;
                bestArea = overlapArea;
            }
        }
        return best;
    }

    /// <summary>Slide the window so at least MinVisible pixels sit inside the screen on both axes.</summary>
    private static Point Nudge(Rectangle bounds, Rectangle area)
    {
        var visibleW = Math.Min(MinVisibleWidth, bounds.Width);
        var visibleH = Math.Min(MinVisibleHeight, bounds.Height);

        var x = Math.Clamp(bounds.X, area.Left - bounds.Width + visibleW, area.Right - visibleW);
        var y = Math.Clamp(bounds.Y, area.Top - bounds.Height + visibleH, area.Bottom - visibleH);
        return new Point(x, y);
    }
}
