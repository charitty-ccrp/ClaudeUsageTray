using ClaudeUsageTray.App;
using ClaudeUsageTray.Models;

namespace ClaudeUsageTray.UI;

internal sealed class SettingsForm : Form
{
    private readonly NumericUpDown _interval = new()
    {
        Minimum = AppConstants.MinRefreshSeconds,
        Maximum = AppConstants.MaxRefreshSeconds,
        Width = 80,
    };
    private readonly CheckBox _startup = new() { AutoSize = true, Text = "Start with Windows (per-user Startup folder)" };
    private readonly CheckBox _notify = new() { AutoSize = true, Text = "Notify me when usage gets high (70% / 85% / 95%) and when a limit resets" };

    private readonly CheckBox _overlayOn = new() { AutoSize = true, Text = "Show desktop overlay" };
    private readonly RadioButton _overlayCompact = new() { AutoSize = true, Text = "Compact (5-hour and weekly bars)" };
    private readonly RadioButton _overlayFull = new() { AutoSize = true, Text = "Full (same details as the popup)" };
    private readonly TrackBar _overlayOpacity = new()
    {
        Minimum = AppConstants.MinOverlayOpacity,
        Maximum = AppConstants.MaxOverlayOpacity,
        TickFrequency = 10,
        SmallChange = 5,
        LargeChange = 10,
        Width = 220,
    };
    private readonly Label _overlayOpacityValue = new() { AutoSize = true };
    private readonly CheckBox _overlayLocked = new()
    {
        AutoSize = true,
        MaximumSize = new Size(340, 0),
        Text = "Lock position (click-through). While locked, unlock it from the tray icon's Overlay menu.",
    };

    private readonly CheckBox _autoLaunchClaudeCode = new()
    {
        AutoSize = true,
        MaximumSize = new Size(360, 0),
        Text = "When Claude Code's login expires, automatically open it in a terminal so it can refresh itself",
    };

    public SettingsForm(AppConfig config, bool startupEnabled, int? credentialAgeDays,
        Func<bool> updateCredential, Action removeCredential, bool credentialAvailable, AuthMode mode,
        bool autoLaunchClaudeCode, Action<int> previewOverlayOpacity, Action resetOverlayPosition)
    {
        Text = "Claude Usage - Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(420, mode == AuthMode.ClaudeCodeOAuth ? 680 : 640);

        _autoLaunchClaudeCode.Checked = autoLaunchClaudeCode;

        _overlayOn.Checked = config.OverlayEnabled;
        _overlayCompact.Checked = config.OverlayCompact;
        _overlayFull.Checked = !config.OverlayCompact;
        _overlayOpacity.Value = Math.Clamp(config.OverlayOpacity, AppConstants.MinOverlayOpacity, AppConstants.MaxOverlayOpacity);
        _overlayOpacityValue.Text = $"{_overlayOpacity.Value}%";
        _overlayLocked.Checked = config.OverlayLocked;

        _interval.Value = Math.Clamp(config.RefreshIntervalSeconds, AppConstants.MinRefreshSeconds, AppConstants.MaxRefreshSeconds);
        _startup.Checked = startupEnabled;
        _notify.Checked = config.NotificationsEnabled;

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16),
        };

        var intervalRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        intervalRow.Controls.Add(new Label { AutoSize = true, Text = "Refresh every (seconds):", Margin = new Padding(0, 6, 6, 0) });
        intervalRow.Controls.Add(_interval);
        panel.Controls.Add(intervalRow);

        _startup.Margin = new Padding(3, 12, 3, 3);
        panel.Controls.Add(_startup);

        _notify.MaximumSize = new Size(380, 0);
        _notify.Margin = new Padding(3, 8, 3, 3);
        panel.Controls.Add(_notify);

        // ----- desktop overlay -----
        var overlayBox = new GroupBox { Text = "Desktop overlay", Width = 380, AutoSize = true, Margin = new Padding(3, 14, 3, 3) };
        var overlayPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8, 4, 8, 8),
        };
        overlayPanel.Controls.Add(_overlayOn);
        _overlayCompact.Margin = new Padding(18, 6, 3, 0);
        _overlayFull.Margin = new Padding(18, 2, 3, 0);
        overlayPanel.Controls.AddRange([_overlayCompact, _overlayFull]);

        var opacityRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(3, 6, 3, 0) };
        opacityRow.Controls.Add(new Label { AutoSize = true, Text = "Opacity", Margin = new Padding(0, 10, 6, 0) });
        opacityRow.Controls.Add(_overlayOpacity);
        _overlayOpacityValue.Margin = new Padding(6, 10, 0, 0);
        opacityRow.Controls.Add(_overlayOpacityValue);
        overlayPanel.Controls.Add(opacityRow);

        _overlayLocked.Margin = new Padding(3, 6, 3, 0);
        overlayPanel.Controls.Add(_overlayLocked);

        var resetPosition = new Button { Text = "Reset position", Width = 130, Height = 28, Margin = new Padding(3, 8, 3, 0) };
        resetPosition.Click += (_, _) => resetOverlayPosition();
        overlayPanel.Controls.Add(resetPosition);
        overlayBox.Controls.Add(overlayPanel);
        panel.Controls.Add(overlayBox);

        // live preview: the overlay follows the slider immediately; Cancel restores the saved value
        _overlayOpacity.ValueChanged += (_, _) =>
        {
            _overlayOpacityValue.Text = $"{_overlayOpacity.Value}%";
            previewOverlayOpacity(_overlayOpacity.Value);
        };

        var credentialText = mode == AuthMode.ClaudeCodeOAuth
            ? "Source: Claude Code login (read-only, nothing stored by this app)"
            : !credentialAvailable
                ? "Source: claude.ai cookie - not set"
                : credentialAgeDays is { } days
                    ? $"Source: claude.ai cookie, saved {days} day(s) ago (sessions last about 30 days)"
                    : "Source: claude.ai cookie, saved";
        panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(360, 0), Text = credentialText, Margin = new Padding(3, 14, 3, 3) });

        if (mode == AuthMode.ClaudeCodeOAuth)
        {
            _autoLaunchClaudeCode.Margin = new Padding(3, 6, 3, 3);
            panel.Controls.Add(_autoLaunchClaudeCode);
        }

        var credentialButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var update = new Button { Text = "Change source...", Width = 150, Height = 28 };
        var remove = new Button { Text = "Remove saved cookie", Width = 150, Height = 28, Enabled = credentialAvailable };
        update.Click += (_, _) => updateCredential();
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Delete the stored credential? Monitoring stops until you add a new one.",
                    "Remove credential", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
            {
                removeCredential();
                remove.Enabled = false;
            }
        };
        credentialButtons.Controls.AddRange([update, remove]);
        panel.Controls.Add(credentialButtons);

        var version = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 16, 3, 3),
            Text = $"About: Claude 5-Hour Usage Tray Monitor v{version}. Shows the usage percentages reported by Claude through " +
                   "undocumented endpoints that may change or stop working at any time. The \"could run out by\" time is our own " +
                   "estimate from recent readings. Not affiliated with Anthropic.",
        });

        var ok = new Button { Text = "OK", Width = 90, Height = 28, DialogResult = DialogResult.OK, Margin = new Padding(3, 20, 3, 3) };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel, Margin = new Padding(3, 20, 3, 3) };
        var okRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        okRow.Controls.AddRange([ok, cancel]);
        panel.Controls.Add(okRow);
        Controls.Add(panel);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    public int RefreshIntervalSeconds => (int)_interval.Value;
    public bool StartWithWindows => _startup.Checked;
    public bool NotificationsEnabled => _notify.Checked;
    public bool OverlayEnabled => _overlayOn.Checked;
    public bool OverlayCompact => _overlayCompact.Checked;
    public bool OverlayLocked => _overlayLocked.Checked;
    public int OverlayOpacity => _overlayOpacity.Value;
    public bool AutoLaunchClaudeCode => _autoLaunchClaudeCode.Checked;
}
