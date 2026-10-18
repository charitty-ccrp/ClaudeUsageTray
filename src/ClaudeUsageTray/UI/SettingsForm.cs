using ClaudeUsageTray.App;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;

namespace ClaudeUsageTray.UI;

/// <summary>Settings in four short tabs. Nothing is saved until OK; ApplyTo copies the choices into a config.</summary>
internal sealed class SettingsForm : Form
{
    private const int ContentWidth = 380;

    private static readonly (PollingMode Mode, string Text, string Detail)[] PollingChoices =
    [
        (PollingMode.Balanced, "Balanced (recommended)", "Every 1.5 min while you use Claude Code, every 15 min otherwise."),
        (PollingMode.Frequent, "Frequent", "Every minute while you use Claude Code, every 5 min otherwise."),
        (PollingMode.Saver, "Saver", "Every 3 min while you use Claude Code, every 30 min otherwise."),
    ];

    private readonly ComboBox _polling = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly CheckBox _startup = new() { AutoSize = true, Text = "Start with Windows" };
    private readonly CheckBox _shortcuts = new() { AutoSize = true, Text = "Shortcuts on the Start menu and desktop" };
    private readonly CheckBox _updates = new() { AutoSize = true, Text = "Tell me when a new version is available" };
    private readonly CheckBox _autoLaunchClaudeCode = new()
    {
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        Text = "When Claude Code's login expires, renew it automatically in the background (no window)",
    };

    private readonly CheckBox _localEstimate = new()
    {
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        Text = "Between checks, show an estimate from local Claude Code use",
    };

    private readonly CheckBox _notify = new() { AutoSize = true, Text = "Show notifications" };
    private readonly CheckBox _notifyWarning = new() { AutoSize = true, Text = "When usage reaches 70% (yellow)" };
    private readonly CheckBox _notifyHigh = new() { AutoSize = true, Text = "When usage reaches 85% (orange)" };
    private readonly CheckBox _notifyCritical = new() { AutoSize = true, Text = "When usage reaches 95% (red)" };
    private readonly CheckBox _notifyReset = new() { AutoSize = true, Text = "When a limit resets after a warning" };
    private readonly CheckBox _quiet = new() { AutoSize = true, Text = "Quiet hours, from" };
    private readonly ComboBox _quietStart = HourPicker();
    private readonly ComboBox _quietEnd = HourPicker();

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
        MaximumSize = new Size(ContentWidth, 0),
        Text = "Lock position and let clicks pass through. Unlock it from the tray icon's Overlay menu.",
    };

    public SettingsForm(AppConfig config, bool startupEnabled, int? credentialAgeDays,
        Func<bool> updateCredential, Action removeCredential, bool credentialAvailable, AuthMode mode,
        bool claudeCliAvailable, Action<int> previewOverlayOpacity, Action resetOverlayPosition, bool shortcutsExist = false)
    {
        _shortcuts.Checked = shortcutsExist;
        Text = "Claude Usage - Settings";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var tabs = new TabControl { Width = ContentWidth + 40, Height = 410 };
        tabs.TabPages.Add(GeneralTab(config, startupEnabled, credentialAgeDays, updateCredential, removeCredential,
            credentialAvailable, mode, claudeCliAvailable));
        tabs.TabPages.Add(NotificationsTab(config));
        tabs.TabPages.Add(OverlayTab(config, previewOverlayOpacity, resetOverlayPosition));
        tabs.TabPages.Add(AboutTab());

        var ok = new Button { Text = "OK", Width = 90, Height = 28, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.AddRange([cancel, ok]);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(12) };
        root.Controls.Add(tabs);
        root.Controls.Add(buttons);
        Controls.Add(root);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>Copies the user's choices into <paramref name="config"/>.</summary>
    public void ApplyTo(AppConfig config)
    {
        config.PollingMode = PollingChoices[Math.Max(0, _polling.SelectedIndex)].Mode;
        config.StartWithWindows = _startup.Checked;
        config.CheckForUpdates = _updates.Checked;
        config.ShowLocalEstimate = _localEstimate.Checked;
        if (_autoLaunchClaudeCode.Enabled) config.AutoLaunchClaudeCode = _autoLaunchClaudeCode.Checked; // keep the choice while the CLI is missing
        config.NotificationsEnabled = _notify.Checked;
        config.NotifyWarning = _notifyWarning.Checked;
        config.NotifyHigh = _notifyHigh.Checked;
        config.NotifyCritical = _notifyCritical.Checked;
        config.NotifyReset = _notifyReset.Checked;
        config.QuietHoursEnabled = _quiet.Checked;
        config.QuietStartHour = Math.Max(0, _quietStart.SelectedIndex);
        config.QuietEndHour = Math.Max(0, _quietEnd.SelectedIndex);
        config.OverlayEnabled = _overlayOn.Checked;
        config.OverlayCompact = _overlayCompact.Checked;
        config.OverlayLocked = _overlayLocked.Checked;
        config.OverlayOpacity = _overlayOpacity.Value;
    }

    public bool StartWithWindows => _startup.Checked;

    public bool Shortcuts => _shortcuts.Checked;

    // ---------- tabs ----------

    private TabPage GeneralTab(AppConfig config, bool startupEnabled, int? credentialAgeDays, Func<bool> updateCredential,
        Action removeCredential, bool credentialAvailable, AuthMode mode, bool claudeCliAvailable)
    {
        var panel = Page();

        panel.Controls.Add(Heading("Data source"));
        var source = mode == AuthMode.ClaudeCodeOAuth
            ? "Claude Code login on this PC (read-only; this app stores nothing)."
            : !credentialAvailable
                ? "claude.ai cookie - not set."
                : credentialAgeDays is { } days
                    ? $"claude.ai cookie, saved {days} day(s) ago. Cookies last about 30 days."
                    : "claude.ai cookie, saved.";
        panel.Controls.Add(Note(source));

        var sourceButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var change = new Button { Text = "Change source...", AutoSize = true, Height = 28 };
        change.Click += (_, _) => updateCredential();
        sourceButtons.Controls.Add(change);
        if (mode == AuthMode.WebCookie)
        {
            var remove = new Button { Text = "Remove saved cookie", AutoSize = true, Height = 28, Enabled = credentialAvailable };
            remove.Click += (_, _) =>
            {
                if (MessageBox.Show(this, "Delete the saved cookie? Monitoring stops until you add a new one.",
                        "Remove cookie", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                removeCredential();
                remove.Enabled = false;
            };
            sourceButtons.Controls.Add(remove);
        }
        panel.Controls.Add(sourceButtons);

        if (mode == AuthMode.ClaudeCodeOAuth)
        {
            _autoLaunchClaudeCode.Checked = config.AutoLaunchClaudeCode && claudeCliAvailable;
            _autoLaunchClaudeCode.Enabled = claudeCliAvailable;
            panel.Controls.Add(WrapText.Fit(_autoLaunchClaudeCode, ContentWidth));
            if (!claudeCliAvailable)
                panel.Controls.Add(Note("The claude command-line tool isn't installed, so the login can't be refreshed for you. " +
                                        "Opening Claude Code or the Claude app does the same job."));
        }
        else
        {
            _autoLaunchClaudeCode.Checked = config.AutoLaunchClaudeCode;
        }

        panel.Controls.Add(Heading("How often to check"));
        foreach (var (_, text, _) in PollingChoices) _polling.Items.Add(text);
        var pollingDetail = Note(string.Empty);
        _polling.SelectedIndexChanged += (_, _) =>
            pollingDetail.Text = PollingChoices[Math.Max(0, _polling.SelectedIndex)].Detail +
                                 " Checking less often when idle avoids \"too many requests\" errors.";
        _polling.SelectedIndex = Math.Max(0, Array.FindIndex(PollingChoices, c => c.Mode == config.PollingMode));
        panel.Controls.Add(_polling);
        panel.Controls.Add(pollingDetail);

        _localEstimate.Checked = config.ShowLocalEstimate;
        panel.Controls.Add(WrapText.Fit(_localEstimate, ContentWidth));
        panel.Controls.Add(Note("Shown as \"≈ 61% (est.)\" until the next real check. It scales Claude's last reading by the " +
                                "tokens in this PC's Claude Code transcripts, so it misses claude.ai and other devices. " +
                                "The icon and notifications always use Claude's real figure."));

        _startup.Checked = startupEnabled;
        _startup.Margin = new Padding(3, 12, 3, 3);
        panel.Controls.Add(_startup);
        panel.Controls.Add(_shortcuts);

        _updates.Checked = config.CheckForUpdates;
        panel.Controls.Add(_updates);

        return Tab("General", panel);
    }

    private TabPage NotificationsTab(AppConfig config)
    {
        var panel = Page();

        _notify.Checked = config.NotificationsEnabled;
        _notifyWarning.Checked = config.NotifyWarning;
        _notifyHigh.Checked = config.NotifyHigh;
        _notifyCritical.Checked = config.NotifyCritical;
        _notifyReset.Checked = config.NotifyReset;
        _quiet.Checked = config.QuietHoursEnabled;
        _quietStart.SelectedIndex = Math.Clamp(config.QuietStartHour, 0, 23);
        _quietEnd.SelectedIndex = Math.Clamp(config.QuietEndHour, 0, 23);

        panel.Controls.Add(_notify);
        CheckBox[] levels = [_notifyWarning, _notifyHigh, _notifyCritical, _notifyReset];
        foreach (var box in levels)
        {
            box.Margin = new Padding(22, 4, 3, 0);
            panel.Controls.Add(box);
        }

        var quietRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(3, 16, 3, 3) };
        _quiet.Margin = new Padding(0, 4, 4, 0);
        quietRow.Controls.Add(_quiet);
        quietRow.Controls.Add(_quietStart);
        quietRow.Controls.Add(new Label { AutoSize = true, Text = "to", Margin = new Padding(6, 6, 6, 0) });
        quietRow.Controls.Add(_quietEnd);
        panel.Controls.Add(quietRow);
        panel.Controls.Add(Note("During quiet hours the icon still updates; only pop-ups are held back."));

        void Sync()
        {
            foreach (var box in levels) box.Enabled = _notify.Checked;
            _quiet.Enabled = _notify.Checked;
            _quietStart.Enabled = _quietEnd.Enabled = _notify.Checked && _quiet.Checked;
        }
        _notify.CheckedChanged += (_, _) => Sync();
        _quiet.CheckedChanged += (_, _) => Sync();
        Sync();

        return Tab("Notifications", panel);
    }

    private TabPage OverlayTab(AppConfig config, Action<int> previewOverlayOpacity, Action resetOverlayPosition)
    {
        var panel = Page();

        _overlayOn.Checked = config.OverlayEnabled;
        _overlayCompact.Checked = config.OverlayCompact;
        _overlayFull.Checked = !config.OverlayCompact;
        _overlayOpacity.Value = Math.Clamp(config.OverlayOpacity, AppConstants.MinOverlayOpacity, AppConstants.MaxOverlayOpacity);
        _overlayOpacityValue.Text = $"{_overlayOpacity.Value}%";
        _overlayLocked.Checked = config.OverlayLocked;

        panel.Controls.Add(Note("A small always-on-top window showing your usage on the desktop."));
        panel.Controls.Add(_overlayOn);
        _overlayCompact.Margin = new Padding(22, 6, 3, 0);
        _overlayFull.Margin = new Padding(22, 2, 3, 0);
        panel.Controls.AddRange([_overlayCompact, _overlayFull]);

        var opacityRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(3, 8, 3, 0) };
        opacityRow.Controls.Add(new Label { AutoSize = true, Text = "Opacity", Margin = new Padding(0, 10, 6, 0) });
        opacityRow.Controls.Add(_overlayOpacity);
        _overlayOpacityValue.Margin = new Padding(6, 10, 0, 0);
        opacityRow.Controls.Add(_overlayOpacityValue);
        panel.Controls.Add(opacityRow);

        _overlayLocked.Margin = new Padding(3, 6, 3, 0);
        panel.Controls.Add(WrapText.Fit(_overlayLocked, ContentWidth));

        var resetPosition = new Button { Text = "Reset position", AutoSize = true, Height = 28, Margin = new Padding(3, 10, 3, 0) };
        resetPosition.Click += (_, _) => resetOverlayPosition();
        panel.Controls.Add(resetPosition);

        // live preview: the overlay follows the slider immediately; Cancel restores the saved value
        _overlayOpacity.ValueChanged += (_, _) =>
        {
            _overlayOpacityValue.Text = $"{_overlayOpacity.Value}%";
            previewOverlayOpacity(_overlayOpacity.Value);
        };

        void Sync() => _overlayCompact.Enabled = _overlayFull.Enabled = _overlayOpacity.Enabled = _overlayLocked.Enabled = _overlayOn.Checked;
        _overlayOn.CheckedChanged += (_, _) => Sync();
        Sync();

        return Tab("Overlay", panel);
    }

    private static TabPage AboutTab()
    {
        var panel = Page();
        var version = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        panel.Controls.Add(new PictureBox { Image = AppIcon.Get().ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize });
        panel.Controls.Add(Heading($"Claude Usage Tray {version}"));
        panel.Controls.Add(Note("Shows the usage percentages Claude reports for your 5-hour and weekly limits. They come from " +
                                "undocumented endpoints that may change or stop working at any time. The \"could run out by\" " +
                                "time is this app's own estimate from recent readings."));
        panel.Controls.Add(Note("Not affiliated with Anthropic."));
        return Tab("About", panel);
    }

    // ---------- helpers ----------

    private static FlowLayoutPanel Page() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(8),
    };

    private static TabPage Tab(string text, Control content)
    {
        var page = new TabPage(text) { UseVisualStyleBackColor = true };
        page.Controls.Add(content);
        return page;
    }

    private static Label Heading(string text) => new()
    {
        AutoSize = true,
        Text = text,
        Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold),
        Margin = new Padding(3, 10, 3, 2),
    };

    private static Label Note(string text) => new()
    {
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        ForeColor = SystemColors.GrayText,
        Text = text,
        Margin = new Padding(3, 2, 3, 6),
    };

    private static ComboBox HourPicker()
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
        for (var h = 0; h < 24; h++) box.Items.Add($"{h:00}:00");
        return box;
    }
}
