using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.UI;

/// <summary>
/// Owns an HICON created by Bitmap.GetHicon(). Icon.FromHandle does NOT own the handle, so Dispose must
/// call DestroyIcon explicitly - otherwise every refresh leaks a GDI object.
/// </summary>
internal sealed class TrayIconHandle : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private IntPtr _handle;

    public TrayIconHandle(IntPtr handle)
    {
        _handle = handle;
        Icon = Icon.FromHandle(handle);
    }

    public Icon Icon { get; }

    public void Dispose()
    {
        Icon.Dispose();
        if (_handle != IntPtr.Zero)
        {
            DestroyIcon(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

internal static class TrayIconRenderer
{
    /// <summary>Below this size the number is unreadable, so only the ring is drawn (the number is in the tooltip).</summary>
    public const int MinSizeForNumber = 20;

    /// <summary>
    /// Draws a ring that fills with the percentage, coloured by level, with the number inside when there is room.
    /// The fill amount shows the level without relying on colour. Caller must dispose the result after the
    /// NotifyIcon stops using it.
    /// </summary>
    public static TrayIconHandle Render(int? percent, UsageLevel level, bool stale)
    {
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bitmap = DrawTile(size, percent, level, stale);
        return new TrayIconHandle(bitmap.GetHicon());
    }

    public static string TextFor(int? percent) =>
        percent is null ? "--" : percent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Pixel height of the digits: they sit inside the ring, so "100" must fit its inner circle.</summary>
    public static float FontPixels(int size, string text) => size * (text.Length switch { 1 => 0.52f, 2 => 0.44f, _ => 0.31f });

    /// <summary>
    /// Typographic format: no wrapping and no built-in side padding. The default StringFormat pads each side and wraps,
    /// which pushed the last "0" of "100" onto a second line on a 16px icon.
    /// </summary>
    public static StringFormat CreateTextFormat() =>
        new(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };

    public static Bitmap DrawTile(int size, int? percent, UsageLevel level, bool stale)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var withNumber = size >= MinSizeForNumber;
        var stroke = size * (withNumber ? 0.14f : 0.24f);
        var inset = stroke / 2 + 0.5f;
        var ring = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);

        // dark disc so the icon reads the same on light and dark taskbars
        using (var disc = new SolidBrush(AppIcon.Tile))
            g.FillEllipse(disc, 0, 0, size, size);
        using (var track = new Pen(Color.FromArgb(0x55, 0x55, 0x55), stroke))
            g.DrawEllipse(track, ring);

        var arcColor = UsageColor.Bar(level, dark: true);
        if (stale) arcColor = Color.FromArgb(150, arcColor);
        if (percent is { } p && p > 0)
        {
            using var arc = new Pen(arcColor, stroke);
            g.DrawArc(arc, ring, -90, 360f * Math.Clamp(p, 0, 100) / 100f);
        }

        var text = percent is null ? "!" : withNumber ? TextFor(percent) : null;
        if (text is not null)
        {
            using var font = new Font("Segoe UI", FontPixels(size, text), FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(AppIcon.Ink);
            using var format = CreateTextFormat();
            g.DrawString(text, font, brush, new RectangleF(0, size * 0.03f, size, size), format);
        }

        if (stale)
        {
            // small light dot in the corner marks "last known value"
            var dot = Math.Max(4f, size * 0.28f);
            using var ink = new SolidBrush(AppIcon.Ink);
            using var edge = new Pen(AppIcon.Tile, Math.Max(1f, size / 16f));
            g.FillEllipse(ink, size - dot - 0.5f, size - dot - 0.5f, dot, dot);
            g.DrawEllipse(edge, size - dot - 0.5f, size - dot - 0.5f, dot, dot);
        }
        return bitmap;
    }
}
