using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.UI;

/// <summary>
/// First-run / change-source dialog. Two sources: Claude Code OAuth (reads Claude Code's own login, stores nothing)
/// or a pasted claude.ai sessionKey (stored encrypted, never shown again).
/// </summary>
internal sealed class SetupForm : Form
{
    private const string CookieAck =
        "I understand this uses an undocumented claude.ai endpoint with my session cookie, which may " +
        "conflict with Anthropic's Terms of Service or my organization's policy, and I use it at my own risk.";

    private const string OAuthAck =
        "I understand this reads my Claude Code OAuth token (read-only, from ~/.claude/.credentials.json) and calls an " +
        "undocumented api.anthropic.com endpoint. Anthropic's terms may restrict using subscription OAuth tokens in " +
        "other tools or my organization's policy may forbid it, and I use it at my own risk.";

    private readonly ClaudeUsageClient _web;
    private readonly ClaudeCodeOAuthClient _oauth;
    private readonly CredentialStore _credentials;
    private readonly ConfigStore _configStore;
    private readonly AppConfig _config;
    private readonly CancellationTokenSource _cts = new();

    // AutoSize does not wrap radio/check boxes, so they get a fixed size tall enough for two/five lines
    private readonly RadioButton _modeOAuth = new()
    {
        AutoSize = false,
        Size = new Size(440, 40),
        Text = "Claude Code login (OAuth) - use this if claude.ai blocks the app with HTTP 403",
    };
    private readonly RadioButton _modeCookie = new() { AutoSize = true, Text = "claude.ai session cookie (sessionKey)" };

    private readonly FlowLayoutPanel _cookiePanel = Vertical();
    private readonly FlowLayoutPanel _oauthPanel = Vertical();
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Width = 440 };
    private readonly ComboBox _org = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 440 };
    private readonly CheckBox _accept = new() { AutoSize = false, Size = new Size(440, 110) };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(440, 0) };
    private readonly Button _test = new() { Text = "Test Connection", Width = 130, Height = 28 };
    private readonly Button _save = new() { Text = "Save", Width = 90, Height = 28 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };

    public SetupForm(ClaudeUsageClient web, ClaudeCodeOAuthClient oauth, CredentialStore credentials,
        ConfigStore configStore, AppConfig config)
    {
        _web = web;
        _oauth = oauth;
        _credentials = credentials;
        _configStore = configStore;
        _config = config;

        Text = "Claude Usage - Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 680);
        CancelButton = _cancel;

        if (!string.IsNullOrEmpty(config.OrganizationId)) _org.Text = config.OrganizationId;

        BuildCookiePanel();
        BuildOAuthPanel();

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16),
        };
        panel.Controls.Add(new Label { AutoSize = true, Text = "Where should usage data come from?", Font = new Font(Font, FontStyle.Bold) });
        panel.Controls.Add(_modeOAuth);
        panel.Controls.Add(_modeCookie);
        panel.Controls.Add(_oauthPanel);
        panel.Controls.Add(_cookiePanel);
        _accept.Margin = new Padding(3, 12, 3, 6);
        panel.Controls.Add(_accept);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([_test, _save, _cancel]);
        panel.Controls.Add(buttons);
        _result.Margin = new Padding(3, 10, 3, 3);
        panel.Controls.Add(_result);
        Controls.Add(panel);

        _modeOAuth.CheckedChanged += (_, _) => ApplyMode();
        _modeCookie.CheckedChanged += (_, _) => ApplyMode();
        (_config.AuthMode == AuthMode.ClaudeCodeOAuth ? _modeOAuth : _modeCookie).Checked = true;
        ApplyMode();

        _test.Click += async (_, _) => await TestAsync();
        _save.Click += (_, _) => Save();
    }

    private AuthMode SelectedMode => _modeOAuth.Checked ? AuthMode.ClaudeCodeOAuth : AuthMode.WebCookie;

    private void BuildOAuthPanel()
    {
        _oauthPanel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Margin = new Padding(3, 8, 3, 3),
            Text = "Uses the login that Claude Code already keeps on this PC (~/.claude/.credentials.json). " +
                   "This app only reads it at each refresh: it never copies, stores, logs or refreshes the token, so " +
                   "nothing is saved here. Claude Code renews its own token; if it has expired, use Claude Code once " +
                   "and this app recovers automatically. No sessionKey or organization ID is needed.",
        });
    }

    private void BuildCookiePanel()
    {
        _cookiePanel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Margin = new Padding(3, 8, 3, 3),
            Text = "Paste your claude.ai session cookie:\r\n" +
                   "1. Open https://claude.ai and sign in.\r\n" +
                   "2. Press F12 > Application (Chrome/Edge) or Storage (Firefox) > Cookies > https://claude.ai.\r\n" +
                   "3. Copy the Value of the cookie named 'sessionKey' (starts with sk-ant-sid).\r\n" +
                   "The cookie lasts about 30 days. It is stored encrypted (Windows DPAPI, current user only). " +
                   "Note: claude.ai may block this app with a Cloudflare challenge (HTTP 403).",
        });
        _cookiePanel.Controls.Add(Caption("Claude session credential (sessionKey)"));
        _cookiePanel.Controls.Add(_key);
        _cookiePanel.Controls.Add(Caption("Organization ID (auto-detected by Test Connection, or type it)"));
        _cookiePanel.Controls.Add(_org);
        _cookiePanel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Tip: on claude.ai open F12 > Application > Cookies > https://claude.ai and copy the value of " +
                   "'lastActiveOrg' (a GUID like 123e4567-e89b-12d3-a456-426614174000).",
        });
    }

    private void ApplyMode()
    {
        var oauth = SelectedMode == AuthMode.ClaudeCodeOAuth;
        _oauthPanel.Visible = oauth;
        _cookiePanel.Visible = !oauth;
        _accept.Text = oauth ? OAuthAck : CookieAck;
        _accept.Checked = _config.DisclaimerAccepted && _config.AuthMode == SelectedMode;
        _result.Text = string.Empty;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        _key.Clear();
        base.OnFormClosed(e);
    }

    private async Task TestAsync()
    {
        SetBusy(true);
        try
        {
            if (SelectedMode == AuthMode.ClaudeCodeOAuth)
                await TestOAuthAsync();
            else
                await TestCookieAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private async Task TestOAuthAsync()
    {
        Show("Reading Claude Code login and fetching usage...", error: false);
        var usage = await _oauth.GetUsageAsync(_cts.Token);
        ShowUsageResult(usage);
    }

    private async Task TestCookieAsync()
    {
        var key = ClaudeUsageClient.NormalizeSessionKey(_key.Text);
        if (key is null) { Show("Enter the sessionKey value first (format looks invalid).", error: true); return; }

        Show("Looking up organizations...", error: false);
        var discovery = await _web.DiscoverOrganizationsAsync(key, _cts.Token);
        if (discovery.IsSuccess)
        {
            _org.Items.Clear();
            foreach (var org in discovery.Organizations)
                _org.Items.Add(new OrgItem(org));
            if (_org.Items.Count > 0 && ClaudeUsageClient.NormalizeOrganizationId(_org.Text) is null)
                _org.SelectedIndex = 0;
        }
        else
        {
            Logger.Warn($"Organization discovery failed: {discovery.Error} - {discovery.Message}");
            Show($"Could not detect the organization automatically: {discovery.Message}\r\n" +
                 "You can type the organization ID manually (see the tip above), then Test again.", error: true);
            if (discovery.Error is UsageErrorKind.Authentication or UsageErrorKind.Forbidden) return;
        }

        var orgId = SelectedOrganizationId();
        if (orgId is null) { Show("Organization ID is required.", error: true); return; }

        Show("Fetching usage...", error: false);
        ShowUsageResult(await _web.FetchUsageAsync(key, orgId, _cts.Token));
    }

    private void ShowUsageResult(UsageFetchResult usage)
    {
        if (usage.IsSuccess)
        {
            Show($"Connection OK. 5-hour usage: {UsageMath.DisplayUtilization(usage.Usage!.FiveHour.Utilization)}%.", error: false);
            return;
        }
        Logger.Warn($"Setup test failed: {usage.Error} - {usage.Message}");
        Show(usage.Message ?? UsageText.LongError(usage.Error), error: true);
    }

    private void Save()
    {
        if (!_accept.Checked) { Show("Please tick the acknowledgement first.", error: true); return; }

        if (SelectedMode == AuthMode.ClaudeCodeOAuth)
        {
            _config.AuthMode = AuthMode.ClaudeCodeOAuth;
            _config.DisclaimerAccepted = true;
            if (!TrySaveConfig()) return;
        }
        else
        {
            var key = ClaudeUsageClient.NormalizeSessionKey(_key.Text);
            if (key is null) { Show("The sessionKey value is missing or has an invalid format.", error: true); return; }
            var orgId = SelectedOrganizationId();
            if (orgId is null) { Show("Enter a valid organization ID (use Test Connection to detect it).", error: true); return; }

            try
            {
                _credentials.Save(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                Logger.Error($"Saving credential failed: {ex.GetType().Name}");
                Show("Could not save the credential securely.", error: true);
                return;
            }
            _config.AuthMode = AuthMode.WebCookie;
            _config.OrganizationId = orgId;
            _config.CredentialSavedAt = DateTimeOffset.UtcNow;
            _config.DisclaimerAccepted = true;
            if (!TrySaveConfig()) return;
        }

        _key.Clear();
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool TrySaveConfig()
    {
        try
        {
            _configStore.Save(_config);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Saving config failed: {ex.GetType().Name}");
            Show("Could not save the settings.", error: true);
            return false;
        }
    }

    private string? SelectedOrganizationId() =>
        _org.SelectedItem is OrgItem item
            ? item.Info.Id
            : ClaudeUsageClient.NormalizeOrganizationId(_org.Text);

    private void SetBusy(bool busy)
    {
        _test.Enabled = !busy;
        _save.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void Show(string message, bool error)
    {
        _result.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
        _result.Text = message;
    }

    private static FlowLayoutPanel Vertical() => new()
    {
        AutoSize = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Margin = new Padding(0),
    };

    private static Label Caption(string text) => new() { AutoSize = true, Text = text, Margin = new Padding(3, 10, 3, 2) };

    private sealed record OrgItem(OrganizationInfo Info)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(Info.Name) ? Info.Id : $"{Info.Name} ({Info.Id})";
    }
}
