using System.Diagnostics;
using ClaudeUsageTray.App;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.Utilities;
using Microsoft.Win32;

namespace ClaudeUsageTray.UI;

/// <summary>Composition root and tray UI. Talks to UsageService only; never touches HttpClient directly.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly bool _mock;
    private readonly ConfigStore _configStore = new();
    private readonly CredentialStore _credentialStore = new();
    private readonly StartupManager _startup = new();
    private readonly ClaudeUsageClient? _client;
    private readonly ClaudeCodeOAuthClient? _oauthClient;
    private readonly UsageService _service;
    private readonly NotificationPolicy _notifications = new();
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _usageItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _weeklyItem = new() { Enabled = false, Visible = false };
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false, Visible = false };

    // polling follows local Claude Code activity; the watchers only nudge UsageService, they never fetch themselves
    private readonly PollingSchedule _schedule;
    private DebouncedFileWatcher? _activityWatcher;
    private DebouncedFileWatcher? _credentialsWatcher;
    private readonly UsageForm _popup = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    // desktop overlay: created only while enabled; every call into it goes through Guard() so a fault never reaches the tray app
    private OverlayForm? _overlay;
    private readonly ToolStripMenuItem _overlayShowItem = new("Show overlay") { CheckOnClick = false };
    private readonly ToolStripMenuItem _overlayCompactItem = new("Compact size") { CheckOnClick = false };
    private readonly ToolStripMenuItem _overlayFullItem = new("Full size") { CheckOnClick = false };
    private readonly ToolStripMenuItem _overlayLockItem = new("Lock position (click-through)") { CheckOnClick = false };
    private readonly ToolStripMenuItem _overlayResetItem = new("Reset position");

    // Claude Code OAuth mode: auto-open the CLI when the login has expired, so the user does not have to do it by hand.
    // "claude update" is a maintenance subcommand that refreshes the token and exits by itself (the window closes on
    // its own); plain "claude" opens the full interactive session, used only for the manual "Sign in..." menu item,
    // in case the refresh token itself is gone and a real login is needed.
    private readonly ClaudeCodeLauncher _claudeCodeAutoLauncher = new(arguments: "update");
    private readonly ClaudeCodeLauncher _claudeCodeSignInLauncher = new();
    private readonly ToolStripMenuItem _signInItem = new("Sign in to Claude Code...") { Visible = false };
    private readonly System.Windows.Forms.Timer _launchRecheckTimer = new() { Interval = (int)AppConstants.ClaudeCodeLaunchRecheckDelay.TotalMilliseconds };
    private bool _claudeCodeLaunchAttempted;

    private AppConfig _config;
    private string? _sessionKey;
    private UsageState _state = UsageState.Initial;
    private TrayIconHandle? _iconHandle;
    private string _iconKey = string.Empty;
    private Form? _openDialog;
    private bool _cleanedUp;

    public TrayApplicationContext(bool mock)
    {
        _mock = mock;
        _config = _configStore.Load();
        _sessionKey = mock ? null : _credentialStore.Load();

        IUsageProvider provider;
        if (mock)
        {
            provider = new MockUsageProvider();
        }
        else
        {
            _client = new ClaudeUsageClient(() => _sessionKey, () => _config.OrganizationId);
            _oauthClient = new ClaudeCodeOAuthClient();
            provider = new RoutingUsageProvider(() => _config.AuthMode, _client, _oauthClient);
        }

        _schedule = new PollingSchedule(() => _config.PollingMode);
        _service = new UsageService(provider, () => _schedule.Interval(DateTimeOffset.UtcNow));
        _service.StateChanged += OnStateChanged;
        _service.RefreshingChanged += OnRefreshingChanged;

        if (mock) _menu.Items.Add(new ToolStripMenuItem("Mock data") { Enabled = false });
        _menu.Items.AddRange([_usageItem, _weeklyItem, _statusItem]);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Refresh now", null, (_, _) => _service.RequestRefresh());

        var overlayMenu = new ToolStripMenuItem("Overlay");
        overlayMenu.DropDownItems.AddRange([_overlayShowItem, new ToolStripSeparator(), _overlayCompactItem, _overlayFullItem,
            new ToolStripSeparator(), _overlayLockItem, _overlayResetItem]);
        _menu.Items.Add(overlayMenu);
        _overlayShowItem.Click += (_, _) => UpdateOverlayConfig(c => c.OverlayEnabled = !c.OverlayEnabled);
        _overlayCompactItem.Click += (_, _) => UpdateOverlayConfig(c => c.OverlayCompact = true);
        _overlayFullItem.Click += (_, _) => UpdateOverlayConfig(c => c.OverlayCompact = false);
        _overlayLockItem.Click += (_, _) => UpdateOverlayConfig(c => c.OverlayLocked = !c.OverlayLocked);
        _overlayResetItem.Click += (_, _) => UpdateOverlayConfig(c => { c.OverlayX = null; c.OverlayY = null; }, replace: true);
        SyncOverlayMenu();

        _signInItem.Click += (_, _) => SignInToClaudeCode();
        _menu.Items.Add(_signInItem);
        SyncSignInMenuItem();

        _menu.Items.Add("Open Claude", null, (_, _) => OpenClaude());
        _menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        _menu.Items.Add("Copy diagnostics", null, (_, _) => CopyDiagnostics());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _launchRecheckTimer.Tick += (_, _) => { _launchRecheckTimer.Stop(); _service.RequestRefresh(); };

        _popup.RefreshClicked += (_, _) => _service.RequestRefresh();
        _popup.SettingsClicked += (_, _) => ShowSettings();
        _popup.OpenClaudeClicked += (_, _) => OpenClaude();
        _popup.ExitClicked += (_, _) => ExitApp();

        _notifyIcon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _notifyIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };

        _timer.Tick += (_, _) => { _service.Tick(); RefreshUi(); };
        _timer.Start();

        if (!mock) StartWatchers();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        RefreshUi();
        _service.Start();
        ApplyOverlayState(replace: true);

        var needsSetup = _config.AuthMode == AuthMode.ClaudeCodeOAuth ? !_config.DisclaimerAccepted : _sessionKey is null;
        if (!mock && needsSetup)
        {
            // first run: open setup once the message loop is running
            var first = new System.Windows.Forms.Timer { Interval = 300 };
            first.Tick += (_, _) => { first.Stop(); first.Dispose(); ShowSetup(); };
            first.Start();
        }
    }

    private void OnStateChanged(UsageState state)
    {
        _state = state;
        Notify(state);
        RefreshUi();
        MaybeAutoLaunchClaudeCode(state);
    }

    /// <summary>Opens the Claude Code CLI once per failure episode when its login has expired; never more than one window.</summary>
    private void MaybeAutoLaunchClaudeCode(UsageState state)
    {
        if (state.Error == UsageErrorKind.None)
        {
            _claudeCodeLaunchAttempted = false;
            return;
        }
        if (!ClaudeCodeRecoveryPolicy.ShouldAutoLaunch(_config.AuthMode, state.Error, _config.AutoLaunchClaudeCode,
                _claudeCodeLaunchAttempted, _claudeCodeAutoLauncher.IsRunning))
            return;

        _claudeCodeLaunchAttempted = true;
        if (!_claudeCodeAutoLauncher.IsAvailable)
        {
            // nothing to run; the credentials watcher refreshes as soon as Claude Code or the Claude app renews the login
            Logger.Info("Claude Code login expired and the claude CLI is not installed; waiting for it to be renewed.");
            return;
        }
        var (started, error) = _claudeCodeAutoLauncher.Launch();
        if (started)
        {
            Logger.Info("Ran 'claude update' so Claude Code can refresh its login; the window closes itself when done.");
            _launchRecheckTimer.Stop();
            _launchRecheckTimer.Start(); // give the CLI a few seconds to refresh the token, then try again sooner than backoff would
            _notifyIcon.ShowBalloonTip(10_000, "Claude Code sign-in needed",
                "Refreshing Claude Code's login in the background. If it does not reconnect, use \"Sign in to Claude Code...\" " +
                "from the tray menu.", ToolTipIcon.Info);
        }
        else
        {
            Logger.Warn($"Could not auto-launch Claude Code: {error}"); // avoid spamming a balloon on every retry
        }
    }

    /// <summary>Manual fallback: a full interactive session, in case "claude update" alone cannot fix it (e.g. logged out).</summary>
    private void SignInToClaudeCode()
    {
        var (started, error) = _claudeCodeSignInLauncher.Launch();
        if (started)
        {
            Logger.Info("Opened an interactive Claude Code session for the user to sign in.");
            _launchRecheckTimer.Stop();
            _launchRecheckTimer.Start();
        }
        else
        {
            MessageBox.Show(error, "Claude Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SyncSignInMenuItem() => _signInItem.Visible = !_mock && _config.AuthMode == AuthMode.ClaudeCodeOAuth;

    private void OnRefreshingChanged(bool refreshing) => RefreshUi();

    /// <summary>Always evaluates (so baselines stay current) but only shows a toast when the user wants them.</summary>
    private void Notify(UsageState state)
    {
        var alerts = NotificationFilter.Apply(_notifications.Evaluate(state), _config, DateTime.Now);
        if (alerts.Count == 0) return;

        var toast = UsageText.Toast(alerts, DateTimeOffset.UtcNow);
        _notifyIcon.ShowBalloonTip(10_000, toast.Title, toast.Text, toast.Warn ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void RefreshUi()
    {
        if (_cleanedUp) return;

        var now = DateTimeOffset.UtcNow;
        var snapshot = _state.Snapshot;

        // the icon always shows the 5-hour session; weekly is in the popup and the right-click menu
        int? percent = snapshot is null ? null : UsageMath.DisplayUtilization(snapshot.FiveHour.Utilization);
        var level = UsageColor.Classify(percent);

        // re-render the icon only when something visible changed (each render allocates a GDI icon)
        // a quiet (brief, transient) failure keeps the normal icon; only a lasting problem marks the value stale
        var showStale = _state.IsStale && !_state.IsQuietFailure;
        var key = $"{percent}|{level}|{showStale}";
        if (key != _iconKey)
        {
            var next = TrayIconRenderer.Render(percent, level, showStale);
            _notifyIcon.Icon = next.Icon;
            _iconHandle?.Dispose();
            _iconHandle = next;
            _iconKey = key;
        }

        _notifyIcon.Text = UsageText.Tooltip(_state, now);

        var fiveHour = snapshot?.FiveHour;
        _usageItem.Text = fiveHour is null
            ? "5-hour: waiting for data"
            : $"5-hour: {UsageMath.DisplayUtilization(fiveHour.Utilization)}% · resets {UsageText.ResetText(_state, now, false)}";

        var sevenDay = snapshot?.Usage.SevenDay;
        _weeklyItem.Visible = sevenDay is not null;
        if (sevenDay is not null)
            _weeklyItem.Text = $"Weekly: {UsageMath.DisplayUtilization(sevenDay.Utilization)}%";

        var problem = _state.Error != UsageErrorKind.None && !_state.IsQuietFailure;
        _statusItem.Visible = problem;
        if (problem) _statusItem.Text = UsageText.ShortError(_state.Error) + (showStale ? " · showing last value" : string.Empty);

        PanelModel? model = null; // built at most once per refresh, shared by the popup and the overlay
        if (_popup.Visible) _popup.UpdateView(model ??= BuildModel(now), _service.IsRefreshing);
        if (_overlay is { Visible: true } overlay) Guard("update", () => overlay.UpdateView(model ??= BuildModel(now)));
    }

    // ---------- desktop overlay ----------

    /// <summary>Runs an overlay operation; on any failure logs it, switches the overlay off and lets the tray app carry on.</summary>
    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Overlay {what} failed, turning the overlay off: {ex.GetType().Name}: {ex.Message}");
            try { _overlay?.Dispose(); } catch (Exception) { /* already failing; nothing more to do */ }
            _overlay = null;
            _config.OverlayEnabled = false;
            SaveConfigQuietly();
            SyncOverlayMenu();
        }
    }

    private void SaveConfigQuietly()
    {
        try
        {
            _configStore.Save(_config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not save settings: {ex.GetType().Name}");
        }
    }

    /// <summary>Change an overlay setting from the tray menu, persist it, and apply it.</summary>
    private void UpdateOverlayConfig(Action<AppConfig> change, bool replace = false)
    {
        change(_config);
        SaveConfigQuietly();
        ApplyOverlayState(replace);
    }

    /// <summary>Makes the overlay match the config: created/shown when enabled, hidden and disposed when not. Idempotent.</summary>
    private void ApplyOverlayState(bool replace = false)
    {
        Guard("update", () =>
        {
            if (_cleanedUp) return;
            SyncOverlayMenu();

            if (!_config.OverlayEnabled)
            {
                if (_overlay is null) return;
                _overlay.Hide();
                _overlay.Dispose();
                _overlay = null;
                return;
            }

            var created = _overlay is null;
            _overlay ??= CreateOverlay();
            _overlay.OpacityPercent = _config.OverlayOpacity;
            _overlay.Locked = _config.OverlayLocked;
            _overlay.Compact = _config.OverlayCompact;
            _overlay.UpdateView(BuildModel(DateTimeOffset.UtcNow));

            if (created || replace) _overlay.PlaceAt(_config.OverlayX, _config.OverlayY);
            if (!_overlay.Visible) _overlay.Show();
        });
    }

    private OverlayForm CreateOverlay()
    {
        var overlay = new OverlayForm();
        overlay.PlacementCommitted += p => Guard("move", () =>
        {
            _config.OverlayX = p.X;
            _config.OverlayY = p.Y;
            SaveConfigQuietly();
        });
        overlay.CompactChanged += compact => Guard("resize", () =>
        {
            if (_config.OverlayCompact == compact) return;
            _config.OverlayCompact = compact;
            SaveConfigQuietly();
        });
        overlay.LockedChanged += locked => Guard("lock", () =>
        {
            if (_config.OverlayLocked == locked) return;
            _config.OverlayLocked = locked;
            SaveConfigQuietly();
            SyncOverlayMenu();
        });
        // hiding disposes the form, so do it after the overlay's own menu handler has returned
        overlay.HideRequested += () => PostToUi(() => UpdateOverlayConfig(c => c.OverlayEnabled = false));
        overlay.DetailsRequested += anchor => Guard("details", () =>
        {
            _popup.UpdateView(BuildModel(DateTimeOffset.UtcNow), _service.IsRefreshing);
            _popup.ShowNearTray(anchor);
        });
        return overlay;
    }

    private void SyncOverlayMenu()
    {
        _overlayShowItem.Checked = _config.OverlayEnabled;
        _overlayCompactItem.Checked = _config.OverlayCompact;
        _overlayFullItem.Checked = !_config.OverlayCompact;
        _overlayLockItem.Checked = _config.OverlayLocked;
        _overlayCompactItem.Enabled = _overlayFullItem.Enabled = _overlayLockItem.Enabled = _overlayResetItem.Enabled = _config.OverlayEnabled;
    }

    private void PostToUi(Action action)
    {
        if (_ui is not null) _ui.Post(_ => { if (!_cleanedUp) action(); }, null);
        else action();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        PostToUi(() => Guard("display change", () => { if (_overlay is { } overlay) overlay.PlaceAt(overlay.Location.X, overlay.Location.Y); }));

    private PanelModel BuildModel(DateTimeOffset now) =>
        PanelModelBuilder.Build(_state, now, _service.BurnRates, SourceBadge(), CredentialAgeDays(), _service.IsRefreshing);

    private string SourceBadge() =>
        _mock ? "Mock data" : _config.AuthMode == AuthMode.ClaudeCodeOAuth ? "Claude Code" : "Cookie";

    private int? CredentialAgeDays() =>
        _config.AuthMode == AuthMode.WebCookie && _config.CredentialSavedAt is { } saved
            ? Math.Max(0, (int)(DateTimeOffset.UtcNow - saved).TotalDays)
            : null;

    private void TogglePopup()
    {
        if (_popup.Visible)
        {
            _popup.Hide();
            return;
        }
        // the click that just deactivated the popup must not immediately re-open it
        if (DateTime.UtcNow - _popup.LastHiddenUtc < TimeSpan.FromMilliseconds(300)) return;

        _popup.UpdateView(BuildModel(DateTimeOffset.UtcNow), _service.IsRefreshing);
        _popup.ShowNearTray(Cursor.Position);
    }

    private void ShowSettings()
    {
        if (ActivateOpenDialog()) return;

        using var form = new SettingsForm(_config, _startup.IsEnabled, CredentialAgeDays(),
            updateCredential: () => ShowSetup(parent: _openDialog),
            removeCredential: RemoveCredential,
            credentialAvailable: _sessionKey is not null,
            mode: _config.AuthMode,
            claudeCliAvailable: _claudeCodeAutoLauncher.IsAvailable,
            previewOverlayOpacity: percent => { if (_overlay is { } overlay) Guard("opacity", () => overlay.OpacityPercent = percent); },
            resetOverlayPosition: () => UpdateOverlayConfig(c => { c.OverlayX = null; c.OverlayY = null; }, replace: true));
        _openDialog = form;
        try
        {
            if (form.ShowDialog() != DialogResult.OK)
            {
                ApplyOverlayState(); // undo the live opacity preview
                return;
            }

            form.ApplyTo(_config);
            if (!_startup.SetEnabled(form.StartWithWindows))
            {
                _config.StartWithWindows = _startup.IsEnabled;
                MessageBox.Show("Could not change the Startup shortcut.", "Claude Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            _configStore.Save(_config);
            ApplyOverlayState();
            _service.RequestRefresh();
        }
        finally
        {
            _openDialog = null;
        }
    }

    private bool ShowSetup(Form? parent = null)
    {
        if (_mock || _client is null || _oauthClient is null)
        {
            MessageBox.Show("Running with --mock; no credential is needed.", "Claude Usage");
            return false;
        }
        if (parent is null && ActivateOpenDialog()) return false;

        using var form = new SetupForm(_client, _oauthClient, _credentialStore, _configStore, _config);
        var previous = _openDialog;
        _openDialog = form;
        try
        {
            var result = parent is null ? form.ShowDialog() : form.ShowDialog(parent);
            if (result != DialogResult.OK) return false;

            _config = _configStore.Load();
            _sessionKey = _credentialStore.Load();
            SyncSignInMenuItem();
            _service.RequestRefresh();
            return true;
        }
        finally
        {
            _openDialog = previous;
        }
    }

    private void RemoveCredential()
    {
        _credentialStore.Delete();
        _sessionKey = null;
        _config.CredentialSavedAt = null;
        _configStore.Save(_config);
        _service.RequestRefresh();
    }

    private bool ActivateOpenDialog()
    {
        if (_openDialog is null || _openDialog.IsDisposed) return false;
        _openDialog.Activate();
        return true;
    }

    private static void OpenClaude()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppConstants.ClaudeHomeUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Warn($"Could not open browser: {ex.GetType().Name}");
        }
    }

    // SystemEvents callbacks arrive on a background thread; RequestRefresh and Paused are thread-safe.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Logger.Info("Resumed from sleep");
        _ = RefreshWhenOnlineAsync();
    }

    /// <summary>After sleep the network can take a while; asking before it is back only produces a "network" error.</summary>
    private async Task RefreshWhenOnlineAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() && DateTime.UtcNow < deadline)
            await Task.Delay(1000).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false); // DNS and Wi-Fi settle just after the link comes up
        _service.RequestRefresh();
    }

    /// <summary>No automatic requests while the PC is locked; one refresh on unlock so the value is fresh.</summary>
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            _service.Paused = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _service.Paused = false;
    }

    private void StartWatchers()
    {
        var claudeDir = Path.GetDirectoryName(ClaudeCodeOAuthClient.DefaultCredentialsPath)!;

        // Claude Code writes its transcripts as you use it: that is when usage moves, so poll more often then
        _activityWatcher = new DebouncedFileWatcher(Path.Combine(claudeDir, "projects"), "*.jsonl",
            includeSubdirectories: true, delay: TimeSpan.FromSeconds(20), OnLocalActivity);

        // Claude Code (or the Claude app) rewrites this file when it renews the login: recover right away
        _credentialsWatcher = new DebouncedFileWatcher(claudeDir, Path.GetFileName(ClaudeCodeOAuthClient.DefaultCredentialsPath),
            includeSubdirectories: false, delay: TimeSpan.FromSeconds(3), () =>
            {
                if (_config.AuthMode != AuthMode.ClaudeCodeOAuth || _service.State.Error == UsageErrorKind.None) return;
                Logger.Info("Claude Code login file changed; refreshing.");
                _service.RequestRefresh();
            });
    }

    private void OnLocalActivity()
    {
        var now = DateTimeOffset.UtcNow;
        _schedule.RecordActivity(now);
        if (!_service.Paused && _schedule.ShouldRefreshOnActivity(_service.State.LastSuccessfulUpdate, now))
            _service.RequestRefresh();
    }

    private void CopyDiagnostics()
    {
        var version = typeof(TrayApplicationContext).Assembly.GetName().Version?.ToString(3) ?? "?";
        var now = DateTimeOffset.UtcNow;
        var lines = new List<string>
        {
            $"Claude Usage Tray {version} on {Environment.OSVersion.VersionString}",
            $"Source: {SourceBadge()} · polling: {_config.PollingMode} ({(_schedule.IsActive(now) ? "active" : "idle")}, every {UsageText.Countdown(_schedule.Interval(now))})",
            $"Claude CLI found: {_claudeCodeAutoLauncher.IsAvailable} · state: {_state.Error}{(_state.IsQuietFailure ? " (quiet)" : string.Empty)}",
            "--- recent log ---",
        };
        lines.AddRange(Logger.Tail(50));
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            _notifyIcon.ShowBalloonTip(3000, "Claude Usage", "Diagnostics copied to the clipboard.", ToolTipIcon.Info);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Logger.Warn($"Could not copy diagnostics: {ex.GetType().Name}");
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e) => _service.RequestRefresh();

    /// <summary>Light/dark switch while running: re-read the setting, repaint the popup and rebuild the tray icon.</summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
            return;

        void Apply()
        {
            if (_cleanedUp) return;
            Theme.Refresh();
            _popup.ApplyTheme();
            if (_overlay is { } overlay) Guard("theme", overlay.ApplyTheme);
            _iconKey = string.Empty;
            RefreshUi();
        }

        if (_ui is not null) _ui.Post(_ => Apply(), null);
        else Apply();
    }

    private void ExitApp()
    {
        Cleanup();
        ExitThread();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.TimeChanged -= OnTimeChanged;
        _activityWatcher?.Dispose();
        _credentialsWatcher?.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        try { _overlay?.Dispose(); } catch (Exception) { /* shutting down anyway */ }
        _overlay = null;
        _timer.Stop();
        _timer.Dispose();
        _launchRecheckTimer.Stop();
        _launchRecheckTimer.Dispose();
        // disposing only stops tracking the process handle; it does not close a CLI window the user is still using
        _claudeCodeAutoLauncher.Dispose();
        _claudeCodeSignInLauncher.Dispose();
        _service.StateChanged -= OnStateChanged;
        _service.RefreshingChanged -= OnRefreshingChanged;
        _service.Dispose();
        _client?.Dispose();
        _oauthClient?.Dispose();

        _notifyIcon.Visible = false; // otherwise the icon lingers in the tray until hovered
        _notifyIcon.Dispose();
        _iconHandle?.Dispose();
        _popup.Dispose();
        _menu.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Cleanup();
        base.Dispose(disposing);
    }
}
