using System.Drawing.Drawing2D;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.UI;

/// <summary>
/// The popup's information area, painted by hand so it can show a proper hierarchy (big 5-hour figure, small weekly bars).
/// It only draws a PanelModel; all wording and numbers are decided elsewhere. Height follows the content, so nothing is clipped.
/// </summary>
internal sealed class UsagePanel : Control
{
    private const int DesignWidth = 360;
    private const TextFormatFlags LineFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags WrapFlags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left;

    private PanelModel? _model;
    private Font _title = null!, _big = null!, _body = null!, _bodyBold = null!, _small = null!, _label = null!;

    public UsagePanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        CreateFonts();
    }

    /// <summary>Repaint only when something visible changed (the model signature includes the ticking footer).</summary>
    public void SetModel(PanelModel model)
    {
        if (_model?.Signature == model.Signature) return;
        _model = model;
        AccessibleName = model.Summary;
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Px(DesignWidth), Measure());

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        DrawAll(e.Graphics);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        CreateFonts();
        Size = GetPreferredSize(Size.Empty);
        base.OnDpiChangedAfterParent(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeFonts();
        base.Dispose(disposing);
    }

    private int Px(float value) => (int)Math.Round(value * DeviceDpi / 96f);

    private int Measure() => Compose(null);

    private void DrawAll(Graphics g) => Compose(g);

    /// <summary>Single source of truth for geometry: with g == null it only measures, otherwise it draws the same rectangles.</summary>
    private int Compose(Graphics? g)
    {
        var m = _model;
        var width = Px(DesignWidth);
        if (m is null) return Px(60);

        var pad = Px(14);
        var inner = width - 2 * pad;
        var y = 0;

        if (g is not null) g.SmoothingMode = SmoothingMode.AntiAlias;

        // ----- header: plan + data-source badge -----
        var headerHeight = Px(44);
        var badgeWidth = TextRenderer.MeasureText(m.Badge, _small, new Size(int.MaxValue, int.MaxValue), LineFlags).Width + Px(26);
        if (g is not null)
        {
            PanelPainter.DrawLine(g, m.Title, _title, Theme.Foreground, new Rectangle(pad, y, inner - badgeWidth - Px(8), headerHeight), LineFlags | TextFormatFlags.VerticalCenter);

            var pill = new Rectangle(width - pad - badgeWidth, y + (headerHeight - Px(20)) / 2, badgeWidth, Px(20));
            using (var track = new SolidBrush(Theme.Track)) PanelPainter.FillPill(g, track, pill);
            var dot = Px(6);
            // the dot doubles as a health light: green = fine, amber = attention, red = failing
            var dotColor = m.Tone switch
            {
                StatusTone.Error => Theme.Error,
                StatusTone.Warning => Theme.Warning,
                _ => UsageColor.Bar(UsageLevel.Normal, Theme.IsDark),
            };
            using (var dotBrush = new SolidBrush(dotColor))
                g.FillEllipse(dotBrush, pill.X + Px(9), pill.Y + (pill.Height - dot) / 2, dot, dot);
            PanelPainter.DrawLine(g, m.Badge, _small, Theme.Muted, new Rectangle(pill.X + Px(20), pill.Y, pill.Width - Px(24), pill.Height), LineFlags | TextFormatFlags.VerticalCenter);
        }
        y += headerHeight;
        Divider(g, y, width, pad);
        y += 1;

        // ----- primary: 5-hour session -----
        if (m.Primary is { } primary)
        {
            y += Px(12);
            if (g is not null) PanelPainter.DrawLine(g, primary.Label, _label, Theme.Muted, new Rectangle(pad, y, inner, Px(16)), LineFlags);
            y += Px(18);

            var bigHeight = Px(40);
            if (g is not null)
            {
                PanelPainter.DrawLine(g, $"{primary.Percent}%", _big, Theme.Foreground, new Rectangle(pad, y, inner / 2, bigHeight), LineFlags | TextFormatFlags.VerticalCenter);
                PanelPainter.DrawLine(g, $"{100 - primary.Percent}% left", _body, Theme.Muted,
                    new Rectangle(pad + inner / 2, y, inner - inner / 2, bigHeight), LineFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
            y += bigHeight + Px(4);

            var barHeight = Px(10);
            if (g is not null) PanelPainter.Bar(g, new Rectangle(pad, y, inner, barHeight), primary.Percent, UsageColor.Bar(primary.Level, Theme.IsDark));
            y += barHeight + Px(8);

            if (primary.Detail.Length > 0)
            {
                if (g is not null) PanelPainter.DrawLine(g, primary.Detail, _body, Theme.Foreground, new Rectangle(pad, y, inner, Px(18)), LineFlags);
                y += Px(18);
            }

            if (m.BurnLine is { } burn)
            {
                var height = TextRenderer.MeasureText(burn, _body, new Size(inner, int.MaxValue), WrapFlags).Height;
                if (g is not null) PanelPainter.DrawLine(g, burn, _body, m.BurnWarn ? Theme.Warning : Theme.Muted, new Rectangle(pad, y, inner, height), WrapFlags);
                y += height + Px(2);
            }

            y += Px(12);
            Divider(g, y, width, pad);
            y += 1;
        }

        // ----- secondary: weekly windows -----
        foreach (var row in m.Others)
        {
            y += Px(10);
            if (g is not null)
            {
                PanelPainter.DrawLine(g, row.Label, _label, Theme.Muted, new Rectangle(pad, y, inner - Px(50), Px(18)), LineFlags | TextFormatFlags.VerticalCenter);
                PanelPainter.DrawLine(g, $"{row.Percent}%", _bodyBold, Theme.Foreground, new Rectangle(width - pad - Px(50), y, Px(50), Px(18)),
                    LineFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
            y += Px(20);

            var barHeight = Px(6);
            if (g is not null) PanelPainter.Bar(g, new Rectangle(pad, y, inner, barHeight), row.Percent, UsageColor.Bar(row.Level, Theme.IsDark));
            y += barHeight + Px(5);

            if (row.Detail.Length > 0)
            {
                if (g is not null) PanelPainter.DrawLine(g, row.Detail, _small, Theme.Muted, new Rectangle(pad, y, inner, Px(16)), LineFlags);
                y += Px(16);
            }
            y += Px(6);
        }
        if (m.Others.Count > 0)
        {
            y += Px(4);
            Divider(g, y, width, pad);
            y += 1;
        }

        // ----- status message -----
        if (m.Status is { } status)
        {
            y += Px(10);
            var height = TextRenderer.MeasureText(status, _body, new Size(inner, int.MaxValue), WrapFlags).Height;
            var color = m.Tone switch { StatusTone.Error => Theme.Error, StatusTone.Warning => Theme.Warning, _ => Theme.Muted };
            if (g is not null) PanelPainter.DrawLine(g, status, _body, color, new Rectangle(pad, y, inner, height), WrapFlags);
            y += height + Px(10);
        }

        // ----- footer -----
        y += Px(4);
        if (g is not null) PanelPainter.DrawLine(g, m.Footer, _small, Theme.Muted, new Rectangle(pad, y, inner, Px(18)), LineFlags | TextFormatFlags.VerticalCenter);
        y += Px(18) + Px(8);
        return y;
    }

    private static void Divider(Graphics? g, int y, int width, int pad)
    {
        if (g is null) return;
        using var pen = new Pen(Theme.Divider);
        g.DrawLine(pen, pad, y, width - pad, y);
    }

    private void CreateFonts()
    {
        DisposeFonts();
        _title = new Font("Segoe UI Semibold", 11f);
        _big = new Font("Segoe UI Semibold", 22f);
        _body = new Font("Segoe UI", 9.5f);
        _bodyBold = new Font("Segoe UI Semibold", 9.5f);
        _small = new Font("Segoe UI", 8.5f);
        _label = new Font("Segoe UI Semibold", 8f);
    }

    private void DisposeFonts()
    {
        _title?.Dispose();
        _big?.Dispose();
        _body?.Dispose();
        _bodyBold?.Dispose();
        _small?.Dispose();
        _label?.Dispose();
    }
}
