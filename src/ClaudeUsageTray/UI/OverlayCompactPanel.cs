using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.UI;

/// <summary>
/// The small overlay: a health dot, "5h" bar with its percentage and, when Claude reports it, a "wk" bar. When the data
/// source is failing the weekly row is replaced by a short problem description. Draws a PanelModel only; no decisions here.
/// </summary>
internal sealed class OverlayCompactPanel : Control
{
    private const int DesignWidth = 230;
    private const TextFormatFlags LineFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

    private PanelModel? _model;
    private Font _label = null!, _value = null!, _note = null!;

    public OverlayCompactPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        CreateFonts();
    }

    public void SetModel(PanelModel model)
    {
        if (_model?.Signature == model.Signature) return;
        _model = model;
        AccessibleName = model.Summary;
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Px(DesignWidth), Compose(null));

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        Compose(e.Graphics);
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

    /// <summary>Measures when g is null, otherwise draws the same rectangles; returns the total height.</summary>
    private int Compose(System.Drawing.Graphics? g)
    {
        var m = _model;
        var width = Px(DesignWidth);
        var pad = Px(10);
        var rowHeight = Px(22);
        var gap = Px(4);

        var showSecond = m is not null && (m.ShortStatus is not null || m.Others.Count > 0);
        var height = pad + rowHeight + (showSecond ? gap + rowHeight : 0) + pad;
        if (g is null || m is null) return height;

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var dotColor = m.Tone switch
        {
            StatusTone.Error => Theme.Error,
            StatusTone.Warning => Theme.Warning,
            _ => UsageColor.Bar(UsageLevel.Normal, Theme.IsDark),
        };
        var y = pad;

        // ----- row 1: 5-hour -----
        var dot = Px(6);
        using (var dotBrush = new SolidBrush(dotColor))
            g.FillEllipse(dotBrush, pad, y + (rowHeight - dot) / 2, dot, dot);
        Row(g, m.Primary, "5h", pad + Px(14), y, width, pad, rowHeight, m.IsStale);
        y += rowHeight + gap;

        // ----- row 2: weekly, or the short problem text -----
        if (m.ShortStatus is { } problem)
        {
            PanelPainter.DrawLine(g, problem + (m.Primary is not null ? " - last value shown" : string.Empty), _note, Theme.Error,
                new Rectangle(pad + Px(14), y, width - 2 * pad - Px(14), rowHeight), LineFlags | TextFormatFlags.VerticalCenter);
        }
        else if (m.Others.Count > 0)
        {
            Row(g, m.Others[0], "wk", pad + Px(14), y, width, pad, rowHeight, m.IsStale);
        }
        return height;
    }

    private void Row(System.Drawing.Graphics g, WindowRow? row, string label, int x, int y, int width, int pad, int rowHeight, bool stale)
    {
        var labelWidth = Px(24);
        var valueWidth = Px(40);
        PanelPainter.DrawLine(g, label, _label, Theme.Muted, new Rectangle(x, y, labelWidth, rowHeight), LineFlags | TextFormatFlags.VerticalCenter);

        var barX = x + labelWidth + Px(4);
        var barRight = width - pad - valueWidth - Px(6);
        var barHeight = Px(8);
        var bar = new Rectangle(barX, y + (rowHeight - barHeight) / 2, Math.Max(Px(20), barRight - barX), barHeight);

        var percent = row?.Percent ?? 0;
        var barColor = row is null ? Theme.Track : UsageColor.Bar(row.Level, Theme.IsDark);
        PanelPainter.Bar(g, bar, percent, barColor);

        var text = row is null ? "--" : $"{row.Percent}%";
        PanelPainter.DrawLine(g, text, _value, stale ? Theme.Muted : Theme.Foreground,
            new Rectangle(width - pad - valueWidth, y, valueWidth, rowHeight),
            LineFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    private void CreateFonts()
    {
        DisposeFonts();
        _label = new Font("Segoe UI Semibold", 8.5f);
        _value = new Font("Segoe UI Semibold", 10f);
        _note = new Font("Segoe UI", 8.5f);
    }

    private void DisposeFonts()
    {
        _label?.Dispose();
        _value?.Dispose();
        _note?.Dispose();
    }
}
