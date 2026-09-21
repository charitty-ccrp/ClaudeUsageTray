using System.ComponentModel;
using System.Runtime.InteropServices;
using ClaudeUsageTray.App;

namespace ClaudeUsageTray.UI;

/// <summary>
/// Always-on-top desktop overlay. Never takes focus from what the user is typing in, stays out of Alt+Tab and the taskbar,
/// can be dragged, made translucent, and locked into a click-through state (mouse events fall through to the windows below).
/// Pure view: the tray context owns the data, persistence and the on/off decision.
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int GwlExStyle = -20;
    private const uint LwaAlpha = 0x2;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRoundCorners = 2;
    private const int DragThreshold = 3;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly UsagePanel _full = new();
    private readonly OverlayCompactPanel _compact = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _compactItem = new("Compact size") { CheckOnClick = false };
    private readonly ToolStripMenuItem _fullItem = new("Full size") { CheckOnClick = false };
    private readonly ToolStripMenuItem _lockItem = new("Lock position (click-through)") { CheckOnClick = false };

    private PanelModel? _model;
    private bool _compactMode = true;
    private bool _locked;
    private int _opacityPercent = AppConstants.DefaultOverlayOpacity;

    private Point _dragStartCursor;
    private Point _dragStartLocation;
    private bool _pressed;
    private bool _dragged;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None; // panels size themselves from DeviceDpi
        Text = "Claude Usage Overlay";

        Controls.AddRange([_full, _compact]);
        _full.Visible = false;

        _compactItem.Click += (_, _) => Compact = true;
        _fullItem.Click += (_, _) => Compact = false;
        _lockItem.Click += (_, _) => Locked = true;
        _menu.Items.AddRange([_compactItem, _fullItem, new ToolStripSeparator(), _lockItem, new ToolStripSeparator()]);
        _menu.Items.Add("Show details...", null, (_, _) => DetailsRequested?.Invoke(CenterOnScreen()));
        _menu.Items.Add("Hide overlay", null, (_, _) => HideRequested?.Invoke());
        _menu.Opening += (_, _) =>
        {
            _compactItem.Checked = _compactMode;
            _fullItem.Checked = !_compactMode;
        };

        foreach (Control control in new Control[] { this, _full, _compact })
        {
            control.ContextMenuStrip = _menu;
            control.MouseDown += OnPress;
            control.MouseMove += OnMove;
            control.MouseUp += OnRelease;
            control.DoubleClick += (_, _) => { if (!_locked) DetailsRequested?.Invoke(CenterOnScreen()); };
        }

        ApplyTheme();
    }

    /// <summary>The position was changed by the user dragging (raised once, on release).</summary>
    public event Action<Point>? PlacementCommitted;
    public event Action<bool>? CompactChanged;
    public event Action<bool>? LockedChanged;
    public event Action? HideRequested;

    /// <summary>Asks the owner to show the full popup near the given screen point.</summary>
    public event Action<Point>? DetailsRequested;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Compact
    {
        get => _compactMode;
        set
        {
            if (_compactMode == value) return;
            _compactMode = value;
            ApplyMode();
            CompactChanged?.Invoke(value);
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Locked
    {
        get => _locked;
        set
        {
            if (_locked == value) return;
            _locked = value;
            ApplyLock();
            LockedChanged?.Invoke(value);
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int OpacityPercent
    {
        get => _opacityPercent;
        set
        {
            _opacityPercent = Math.Clamp(value, AppConstants.MinOverlayOpacity, AppConstants.MaxOverlayOpacity);
            ApplyOpacity();
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // layered is always on (needed for both opacity and click-through); tool window keeps it out of Alt+Tab
            cp.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate;
            if (_locked) cp.ExStyle |= WsExTransparent;
            return cp;
        }
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Background;
        _full.BackColor = Theme.Background;
        _compact.BackColor = Theme.Background;
        Invalidate(true);
    }

    public void UpdateView(PanelModel model)
    {
        _model = model;
        (_compactMode ? (Action<PanelModel>)_compact.SetModel : _full.SetModel)(model);
        Relayout();
    }

    /// <summary>Moves the overlay to the saved position if it is still reachable, otherwise to the default corner.</summary>
    public void PlaceAt(int? x, int? y)
    {
        Relayout();
        Location = OverlayPlacement.Resolve(x, y, Size, WorkingAreas());
    }

    public static IReadOnlyList<Rectangle> WorkingAreas()
    {
        var primary = Screen.PrimaryScreen;
        var areas = new List<Rectangle>();
        if (primary is not null) areas.Add(primary.WorkingArea);
        areas.AddRange(Screen.AllScreens.Where(s => !s.Equals(primary)).Select(s => s.WorkingArea));
        return areas;
    }

    private void ApplyMode()
    {
        _compact.Visible = _compactMode;
        _full.Visible = !_compactMode;
        if (_model is not null) UpdateView(_model);
        else Relayout();
    }

    private void Relayout()
    {
        const int border = 1;
        Control active = _compactMode ? _compact : _full;
        var size = active.GetPreferredSize(Size.Empty);
        active.Bounds = new Rectangle(border, border, size.Width, size.Height);
        var client = new Size(size.Width + 2 * border, size.Height + 2 * border);
        if (ClientSize != client) ClientSize = client;
    }

    private void ApplyOpacity()
    {
        if (!IsHandleCreated) return;
        SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(255 * _opacityPercent / 100.0), LwaAlpha);
    }

    /// <summary>
    /// Click-through must be WS_EX_TRANSPARENT on a layered window: answering WM_NCHITTEST with HTTRANSPARENT only lets
    /// clicks fall through to windows of the same thread, not to other applications.
    /// </summary>
    private void ApplyLock()
    {
        if (!IsHandleCreated) return;
        var style = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        style = _locked ? style | WsExTransparent : style & ~(long)WsExTransparent;
        SetWindowLongPtr(Handle, GwlExStyle, (IntPtr)style);
        Cursor = _locked ? Cursors.Default : Cursors.SizeAll;
    }

    private Point CenterOnScreen() => new(Left + Width / 2, Top + Height / 2);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyOpacity();
        ApplyLock();
        var preference = DwmRoundCorners; // Windows 11 rounded corners; ignored elsewhere
        _ = DwmSetWindowAttribute(Handle, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            m.Result = (IntPtr)MaNoActivate; // a click must never pull focus away from the user's work
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    // ----- drag (done by hand: HTCAPTION would swallow the right-click menu and turn double-click into "maximize") -----

    private void OnPress(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _locked) return;
        _pressed = true;
        _dragged = false;
        _dragStartCursor = Cursor.Position;
        _dragStartLocation = Location;
        if (sender is Control control) control.Capture = true;
    }

    private void OnMove(object? sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        var delta = new Size(Cursor.Position.X - _dragStartCursor.X, Cursor.Position.Y - _dragStartCursor.Y);
        if (!_dragged && Math.Abs(delta.Width) < DragThreshold && Math.Abs(delta.Height) < DragThreshold) return;

        _dragged = true;
        Location = _dragStartLocation + delta;
    }

    private void OnRelease(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_pressed) return;
        _pressed = false;
        if (sender is Control control) control.Capture = false;
        if (!_dragged) return;

        // never leave it stranded off-screen, then remember it (once per drag, not per pixel)
        var placed = OverlayPlacement.Resolve(Location.X, Location.Y, Size, WorkingAreas());
        Location = placed;
        PlacementCommitted?.Invoke(placed);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }
}
