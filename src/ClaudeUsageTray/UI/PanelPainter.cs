using System.Drawing.Drawing2D;

namespace ClaudeUsageTray.UI;

/// <summary>Drawing primitives shared by the popup panel and the overlay panels so they always look the same.</summary>
internal static class PanelPainter
{
    public static void DrawLine(Graphics g, string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags) =>
        TextRenderer.DrawText(g, text, font, bounds, color, flags);

    /// <summary>Rounded progress bar: a track with a fill of at least one bar-height so tiny values still look round.</summary>
    public static void Bar(Graphics g, Rectangle bounds, int percent, Color fill)
    {
        using (var track = new SolidBrush(Theme.Track)) FillPill(g, track, bounds);
        if (percent <= 0) return;

        var fillWidth = Math.Clamp(bounds.Width * percent / 100, bounds.Height, bounds.Width);
        using var brush = new SolidBrush(fill);
        FillPill(g, brush, new Rectangle(bounds.X, bounds.Y, fillWidth, bounds.Height));
    }

    public static void FillPill(Graphics g, Brush brush, Rectangle r)
    {
        var d = r.Height;
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
