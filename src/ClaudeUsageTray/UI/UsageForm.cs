using System.Runtime.InteropServices;

namespace ClaudeUsageTray.UI;

/// <summary>
/// Borderless popup shown on tray click: a UsagePanel plus four buttons. Pure view - the tray context feeds it a PanelModel.
/// Closes on Esc or when it loses focus, and opens beside the taskbar edge nearest the click (any screen, any taskbar side).
/// </summary>
internal sealed class UsageForm : Form
{
    private const int ClassStyleDropShadow = 0x20000;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRoundCorners = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly UsagePanel _panel = new();
    private readonly Button _refresh = MakeButton("Refresh");
    private readonly Button _settings = MakeButton("Settings");
    private readonly Button _openClaude = MakeButton("claude.ai"); // opens https://claude.ai/ in the default browser
    private readonly Button _exit = MakeButton("Exit");
    private Point _anchor;

    public UsageForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None; // the panel and Relayout() size everything from DeviceDpi themselves
        Text = "Claude Usage";

        Controls.AddRange([_panel, _refresh, _settings, _openClaude, _exit]);
        _refresh.Click += (_, _) => RefreshClicked?.Invoke(this, EventArgs.Empty);
        _settings.Click += (_, _) => SettingsClicked?.Invoke(this, EventArgs.Empty);
        _openClaude.Click += (_, _) => OpenClaudeClicked?.Invoke(this, EventArgs.Empty);
        _exit.Click += (_, _) => ExitClicked?.Invoke(this, EventArgs.Empty);

        ApplyTheme();
    }

    public event EventHandler? RefreshClicked;
    public event EventHandler? SettingsClicked;
    public event EventHandler? OpenClaudeClicked;
    public event EventHandler? ExitClicked;

    /// <summary>Used to ignore the tray click that caused this popup to deactivate (otherwise it re-opens at once).</summary>
    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= ClassStyleDropShadow;
            return cp;
        }
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Background;
        _panel.BackColor = Theme.Background;
        Invalidate(true);
    }

    public void UpdateView(PanelModel model, bool refreshing)
    {
        _panel.SetModel(model);
        SetRefreshing(refreshing);
        Relayout();
        if (Visible) PositionNearTray();
    }

    /// <summary>Disabling the button is the feedback; the footer says "Refreshing..." (the label would not fit the button).</summary>
    public void SetRefreshing(bool refreshing) => _refresh.Enabled = !refreshing;

    /// <param name="anchor">Screen position of the tray click; decides which screen and taskbar edge to use.</param>
    public void ShowNearTray(Point anchor)
    {
        _anchor = anchor;
        Relayout();
        PositionNearTray();
        Show();
        Activate();
    }

    private void Relayout()
    {
        var scale = DeviceDpi / 96f;
        int Px(float v) => (int)Math.Round(v * scale);

        const int border = 1;
        var panelSize = _panel.GetPreferredSize(Size.Empty);
        _panel.Bounds = new Rectangle(border, border, panelSize.Width, panelSize.Height);

        var pad = Px(10);
        var gap = Px(6);
        var buttonHeight = Px(30);
        var buttonWidth = (panelSize.Width - 2 * pad - 3 * gap) / 4;
        var y = border + panelSize.Height + Px(2);
        var x = border + pad;
        foreach (var button in new[] { _refresh, _settings, _openClaude, _exit })
        {
            button.SetBounds(x, y, buttonWidth, buttonHeight);
            x += buttonWidth + gap;
        }

        var size = new Size(panelSize.Width + 2 * border, y + buttonHeight + pad);
        if (ClientSize != size) ClientSize = size;
    }

    private void PositionNearTray()
    {
        var screen = Screen.FromPoint(_anchor);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        const int margin = 8;

        int x, y;
        if (work.Bottom < bounds.Bottom)          // taskbar at the bottom
        {
            x = Clamp(_anchor.X - Width / 2, work.Left + margin, work.Right - Width - margin);
            y = work.Bottom - Height - margin;
        }
        else if (work.Top > bounds.Top)           // taskbar at the top
        {
            x = Clamp(_anchor.X - Width / 2, work.Left + margin, work.Right - Width - margin);
            y = work.Top + margin;
        }
        else if (work.Left > bounds.Left)         // taskbar on the left
        {
            x = work.Left + margin;
            y = Clamp(_anchor.Y - Height / 2, work.Top + margin, work.Bottom - Height - margin);
        }
        else if (work.Right < bounds.Right)       // taskbar on the right
        {
            x = work.Right - Width - margin;
            y = Clamp(_anchor.Y - Height / 2, work.Top + margin, work.Bottom - Height - margin);
        }
        else                                      // auto-hide taskbar: fall back to the usual corner
        {
            x = work.Right - Width - margin;
            y = work.Bottom - Height - margin;
        }
        Location = new Point(x, y);
    }

    private static int Clamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var preference = DwmRoundCorners; // Windows 11 rounded corners; harmlessly ignored elsewhere
        _ = DwmSetWindowAttribute(Handle, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Hide();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) LastHiddenUtc = DateTime.UtcNow;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    private static Button MakeButton(string text) => new() { Text = text, AutoEllipsis = true };
}
