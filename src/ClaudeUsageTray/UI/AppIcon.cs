using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ClaudeUsageTray.UI;

/// <summary>
/// The app icon - a gauge ring on a dark rounded square - drawn in code so the window icons, the About tab and the
/// .exe's embedded icon (assets/app.ico, regenerated with "ClaudeUsageTray.exe --write-icon path") stay identical.
/// </summary>
internal static class AppIcon
{
    public static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    internal static readonly Color Tile = Color.FromArgb(0x1F, 0x1E, 0x1D);
    internal static readonly Color Track = Color.FromArgb(0x3A, 0x39, 0x36);
    internal static readonly Color Accent = Color.FromArgb(0xD9, 0x77, 0x57);
    internal static readonly Color Ink = Color.FromArgb(0xF5, 0xF0, 0xE8);

    /// <summary>How much of the ring the icon shows filled; a fixed "about two-thirds used" look.</summary>
    private const float Fill = 0.70f;

    private static Icon? _cached;

    /// <summary>Multi-size icon for forms (Windows picks the best size per DPI). Shared; do not dispose.</summary>
    public static Icon Get()
    {
        if (_cached is not null) return _cached;
        using var stream = new MemoryStream(IcoBytes());
        return _cached = new Icon(stream);
    }

    public static Bitmap Draw(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using (var tile = new SolidBrush(Tile))
        using (var path = RoundedRect(new RectangleF(0, 0, size, size), size * 0.22f))
            g.FillPath(tile, path);

        // small sizes get a thicker ring and no track or label, so the shape still reads at 16px
        var small = size < 32;
        var stroke = size * (small ? 0.19f : 0.11f);
        var radius = size * (small ? 0.29f : 0.30f);
        var ring = new RectangleF(size / 2f - radius, size / 2f - radius, radius * 2, radius * 2);

        if (!small)
        {
            using var trackPen = new Pen(Track, stroke);
            g.DrawEllipse(trackPen, ring);
        }
        using (var arcPen = new Pen(Accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arcPen, ring, -90, 360 * Fill);

        if (size >= 48)
        {
            using var font = new Font("Segoe UI Semibold", size * 0.22f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Ink);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("5h", font, ink, new RectangleF(0, size * 0.01f, size, size), format);
        }
        return bitmap;
    }

    /// <summary>A Windows .ico holding every size in <see cref="Sizes"/> as PNG frames (supported since Vista).</summary>
    public static byte[] IcoBytes()
    {
        var frames = Sizes.Select(size =>
        {
            using var bitmap = Draw(size);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return (Size: size, Data: png.ToArray());
        }).ToArray();

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((short)0);              // reserved
        writer.Write((short)1);              // type: icon
        writer.Write((short)frames.Length);

        var offset = 6 + 16 * frames.Length;
        foreach (var (size, data) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);           // palette
            writer.Write((byte)0);           // reserved
            writer.Write((short)1);          // colour planes
            writer.Write((short)32);         // bits per pixel
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in frames) writer.Write(data);
        writer.Flush();
        return output.ToArray();
    }

    internal static GraphicsPath RoundedRect(RectangleF r, float radius)
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
