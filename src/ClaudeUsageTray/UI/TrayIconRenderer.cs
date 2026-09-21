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
    /// <summary>Draws the percentage on a coloured tile. Caller must dispose the result after the NotifyIcon stops using it.</summary>
    public static TrayIconHandle Render(int? percent, UsageLevel level, bool stale)
    {
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bitmap = DrawTile(size, percent, level, stale);
        return new TrayIconHandle(bitmap.GetHicon());
    }

    public static string TextFor(int? percent) =>
        percent is null ? "--" : percent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Pixel height of the digits: fewer digits can be bigger, three digits ("100") must fit a 16px tile.</summary>
    public static float FontPixels(int size, string text) => size * (text.Length switch { 1 => 0.78f, 2 => 0.64f, _ => 0.46f });

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
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using (var background = new SolidBrush(UsageColor.Background(level)))
        using (var path = RoundedRect(new RectangleF(0, 0, size - 1, size - 1), size * 0.22f))
        {
            g.FillPath(background, path);
        }

        var text = TextFor(percent);
        using var font = new Font("Segoe UI", FontPixels(size, text), FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(UsageColor.Foreground(level));
        using var format = CreateTextFormat();
        g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), format);

        if (stale)
        {
            // thin underline marks "last known value"
            using var pen = new Pen(UsageColor.Foreground(level), Math.Max(1f, size / 10f));
            g.DrawLine(pen, size * 0.2f, size - 2, size * 0.8f, size - 2);
        }
        return bitmap;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
