using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.UI;

/// <summary>
/// First-run / change-source dialog. Two sources: Claude Code OAuth (reads Claude Code's own login, stores nothing)
/// or a pasted claude.ai sessionKey (stored encrypted, never shown again). When a Claude Code login is found the
/// dialog preselects it and tests it straight away, so setup is one click; the cookie route sits under "Advanced".
/// </summary>
internal sealed class SetupForm : Form
{
    private const int ContentWidth = 440;

    private const string ShortAck = "I understand this uses an unofficial Claude endpoint and I use it at my own risk.";

    private const string CookieAck =
        "This uses an undocumented claude.ai endpoint with your session cookie, which may conflict with Anthropic's " +
        "Terms of Service or your organization's policy. The cookie is stored encrypted for your Windows account only.";

    private const string OAuthAck =
        "This reads your Claude Code OAuth token (read-only, from ~/.claude/.credentials.json) and calls an undocumented " +
        "api.anthropic.com endpoint. Anthropic's terms may restrict using subscription OAuth tokens in other tools, or " +
        "your organization's policy may forbid it. The token is never copied, stored, logged or refreshed by this app.";

    private readonly ClaudeUsageClient _web;
    private readonly ClaudeCodeOAuthClient _oauth;
    private readonly CredentialStore _credentials;
    private readonly ConfigStore _configStore;
    private readonly AppConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _claudeCodeFound = File.Exists(ClaudeCodeOAuthClient.DefaultCredentialsPath);

    private readonly RadioButton _modeOAuth = new() { AutoSize = true, Text = "Use my Claude Code login (recommended)" };
    private readonly RadioButton _modeCookie = new() { AutoSize = true, Text = "Use a claude.ai session cookie" };
    private readonly LinkLabel _advanced = new() { AutoSize = true, Text = "Advanced: use a claude.ai cookie instead", Margin = new Padding(3, 10, 3, 3) };

    private readonly FlowLayoutPanel _cookiePanel = Vertical();
    private readonly FlowLayoutPanel _oauthPanel = Vertical();
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Width = ContentWidth - 20 };
    private readonly ComboBox _org = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = ContentWidth - 20 };
    private readonly CheckBox _accept = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Text = ShortAck };
    private readonly LinkLabel _details = new() { AutoSize = true, Text = "What does this mean?", Margin = new Padding(20, 0, 3, 3) };
    private readonly Label _detailsText = new()
    {
        AutoSize = true,
        MaximumSize = new Size(ContentWidth - 20, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(20, 0, 3, 6),
        Visible = false,
    };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(3, 10, 3, 3) };
    private readonly Button _test = new() { Text = "Test connection", AutoSize = true, Height = 30 };
    private readonly Button _save = new() { Text = "Start monitoring", AutoSize = true, Height = 30 };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Height = 30, DialogResult = DialogResult.Cancel };

    public SetupForm(ClaudeUsageClient web, ClaudeCodeOAuthClient oauth, CredentialStore credentials,
        ConfigStore configStore, AppConfig config)
    {
        _web = web;
        _oauth = oauth;
        _credentials = credentials;
        _configStore = configStore;
        _config = config;

        Text = "Claude Usage - Setup";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        CancelButton = _cancel;
        AcceptButton = _save;

        if (!string.IsNullOrEmpty(config.OrganizationId)) _org.Text = config.OrganizationId;

        BuildCookiePanel();
        BuildOAuthPanel();

        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(16),
        };

        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        header.Controls.Add(new PictureBox { Image = AppIcon.Draw(48), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(3, 3, 12, 3) });
        var titles = Vertical();
        titles.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Welcome to Claude Usage Tray",
            Font = new Font(Font.FontFamily, Font.Size * 1.35f, FontStyle.Bold),
            Margin = new Padding(3, 4, 3, 2),
        });
        titles.Controls.Add(new Label { AutoSize = true, Text = "Choose where your usage numbers come from.", ForeColor = SystemColors.GrayText });
        header.Controls.Add(titles);
        panel.Controls.Add(header);

        panel.Controls.Add(_modeOAuth);
        panel.Controls.Add(_oauthPanel);
        panel.Controls.Add(_advanced);
        panel.Controls.Add(_modeCookie);
        panel.Controls.Add(_cookiePanel);

        _accept.Margin = new Padding(3, 14, 3, 0);
        panel.Controls.Add(WrapText.Fit(_accept, ContentWidth));
        panel.Controls.Add(_details);
        panel.Controls.Add(_detailsText);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([_save, _test, _cancel]);
        panel.Controls.Add(buttons);
        panel.Controls.Add(_result);
        Controls.Add(panel);

        _modeOAuth.CheckedChanged += (_, _) => ApplyMode();
        _modeCookie.CheckedChanged += (_, _) => ApplyMode();
        _advanced.LinkClicked += (_, _) => { _advanced.Visible = false; _modeCookie.Visible = true; _modeCookie.Checked = true; };
        _details.LinkClicked += (_, _) => _detailsText.Visible = !_detailsText.Visible;

        // the cookie route is advanced: shown up front only when it is already in use or Claude Code is not installed
        var cookieFirst = _config.AuthMode == AuthMode.WebCookie && (_config.DisclaimerAccepted || !_claudeCodeFound);
        _modeCookie.Visible = cookieFirst;
        _advanced.Visible = !cookieFirst;
        (cookieFirst ? _modeCookie : _modeOAuth).Checked = true;
        ApplyMode();

        _test.Click += async (_, _) => await TestAsync();
        _save.Click += (_, _) => Save();

        // one-click setup: when Claude Code's login is here, check it right away so the user just presses Start
        Shown += async (_, _) =>
        {
            if (SelectedMode == AuthMode.ClaudeCodeOAuth && _claudeCodeFound) await TestAsync();
        };
    }

    private AuthMode SelectedMode => _modeOAuth.Checked ? AuthMode.ClaudeCodeOAuth : AuthMode.WebCookie;

    private void BuildOAuthPanel()
    {
        _oauthPanel.Margin = new Padding(20, 0, 3, 3);
        _oauthPanel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 20, 0),
            ForeColor = _claudeCodeFound ? SystemColors.ControlText : Color.Firebrick,
            Margin = new Padding(3, 2, 3, 3),
            Text = _claudeCodeFound
                ? "Found a Claude Code login on this PC. Nothing is copied or stored, and it keeps working when Claude Code renews its login."
                : "No Claude Code login found on this PC. Sign in to Claude Code (or the Claude desktop app's Code tab) first, then reopen this window.",
        });
    }

    private void BuildCookiePanel()
    {
        _cookiePanel.Margin = new Padding(20, 0, 3, 3);
        _cookiePanel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 20, 0),
            Margin = new Padding(3, 2, 3, 3),
            Text = "1. Open https://claude.ai and sign in.\r\n" +
                   "2. Press F12 > Application (Chrome/Edge) or Storage (Firefox) > Cookies > https://claude.ai.\r\n" +
                   "3. Copy the value of the cookie named 'sessionKey' (starts with sk-ant-sid).\r\n" +
                   "It lasts about 30 days. claude.ai sometimes blocks apps like this one (HTTP 403).",
        });
        _cookiePanel.Controls.Add(Caption("Session cookie (sessionKey)"));
        _cookiePanel.Controls.Add(_key);
        _cookiePanel.Controls.Add(Caption("Organization (found by Test connection, or paste 'lastActiveOrg')"));
        _cookiePanel.Controls.Add(_org);
    }

    private void ApplyMode()
    {
        var oauth = SelectedMode == AuthMode.ClaudeCodeOAuth;
        _oauthPanel.Visible = oauth;
        _cookiePanel.Visible = !oauth;
        _detailsText.Text = oauth ? OAuthAck : CookieAck;
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
        Show("Checking your Claude Code login...", error: false);
        var usage = await _oauth.GetUsageAsync(_cts.Token);
        ShowUsageResult(usage);
    }

    private async Task TestCookieAsync()
    {
        var key = ClaudeUsageClient.NormalizeSessionKey(_key.Text);
        if (key is null) { Show("Paste the sessionKey value first (the format looks wrong).", error: true); return; }

        Show("Looking up your organization...", error: false);
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
            Show($"Couldn't find your organization automatically: {discovery.Message}\r\n" +
                 "Paste the 'lastActiveOrg' cookie value into the Organization box, then test again.", error: true);
            if (discovery.Error is UsageErrorKind.Authentication or UsageErrorKind.Forbidden) return;
        }

        var orgId = SelectedOrganizationId();
        if (orgId is null) { Show("An organization is required.", error: true); return; }

        Show("Fetching usage...", error: false);
        ShowUsageResult(await _web.FetchUsageAsync(key, orgId, _cts.Token));
    }

    private void ShowUsageResult(UsageFetchResult usage)
    {
        if (usage.IsSuccess)
        {
            Show($"Connected. Your 5-hour usage is {UsageMath.DisplayUtilization(usage.Usage!.FiveHour.Utilization)}%. " +
                 "Press Start monitoring to finish.", error: false);
            return;
        }
        Logger.Warn($"Setup test failed: {usage.Error} - {usage.Message}");
        Show(usage.Message ?? UsageText.LongError(usage.Error), error: true);
    }

    private void Save()
    {
        if (!_accept.Checked)
        {
            Show("Tick the box above to continue.", error: true);
            _accept.Focus();
            return;
        }

        if (SelectedMode == AuthMode.ClaudeCodeOAuth)
        {
            _config.AuthMode = AuthMode.ClaudeCodeOAuth;
            _config.DisclaimerAccepted = true;
            if (!TrySaveConfig()) return;
        }
        else
        {
            var key = ClaudeUsageClient.NormalizeSessionKey(_key.Text);
            if (key is null) { Show("The sessionKey value is missing or the format looks wrong.", error: true); return; }
            var orgId = SelectedOrganizationId();
            if (orgId is null) { Show("Enter an organization (Test connection finds it for you).", error: true); return; }

            try
            {
                _credentials.Save(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                Logger.Error($"Saving credential failed: {ex.GetType().Name}");
                Show("Couldn't save the cookie securely.", error: true);
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
            Show("Couldn't save the settings.", error: true);
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
