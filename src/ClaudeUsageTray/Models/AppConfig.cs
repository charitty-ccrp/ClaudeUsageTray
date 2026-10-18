using System.Text.Json.Serialization;
using ClaudeUsageTray.App;

namespace ClaudeUsageTray.Models;

/// <summary>Persisted, non-secret settings. The session credential is NEVER stored here.</summary>
public sealed class AppConfig
{
    [JsonPropertyName("authMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthMode AuthMode { get; set; } = AuthMode.WebCookie;

    [JsonPropertyName("organizationId")]
    public string? OrganizationId { get; set; }

    /// <summary>Replaces the old fixed "refreshIntervalSeconds" (ignored on load, so old configs get Balanced).</summary>
    [JsonPropertyName("pollingMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PollingMode PollingMode { get; set; } = PollingMode.Balanced;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    /// <summary>Date the credential was saved (not the key itself), used to warn before the ~30 day expiry.</summary>
    [JsonPropertyName("credentialSavedAt")]
    public DateTimeOffset? CredentialSavedAt { get; set; }

    [JsonPropertyName("notificationsEnabled")]
    public bool NotificationsEnabled { get; set; } = true;

    // which alerts to show (the levels match the icon colours: 70% yellow, 85% orange, 95% red)
    [JsonPropertyName("notifyWarning")]
    public bool NotifyWarning { get; set; } = true;

    [JsonPropertyName("notifyHigh")]
    public bool NotifyHigh { get; set; } = true;

    [JsonPropertyName("notifyCritical")]
    public bool NotifyCritical { get; set; } = true;

    [JsonPropertyName("notifyReset")]
    public bool NotifyReset { get; set; } = true;

    /// <summary>No pop-ups between QuietStartHour and QuietEndHour (local time, may wrap past midnight).</summary>
    [JsonPropertyName("quietHoursEnabled")]
    public bool QuietHoursEnabled { get; set; }

    [JsonPropertyName("quietStartHour")]
    public int QuietStartHour { get; set; } = 22;

    [JsonPropertyName("quietEndHour")]
    public int QuietEndHour { get; set; } = 7;

    /// <summary>Claude Code OAuth mode only: open the Claude Code CLI automatically when its login has expired.</summary>
    [JsonPropertyName("autoLaunchClaudeCode")]
    public bool AutoLaunchClaudeCode { get; set; } = true;

    /// <summary>Once a day, ask GitHub whether a newer release exists (only shows a menu item; never installs).</summary>
    [JsonPropertyName("checkForUpdates")]
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Between real polls, show a clearly labelled estimate from local Claude Code transcripts. Display only.</summary>
    [JsonPropertyName("showLocalEstimate")]
    public bool ShowLocalEstimate { get; set; }

    [JsonPropertyName("lastUpdateCheck")]
    public DateTimeOffset? LastUpdateCheck { get; set; }

    [JsonPropertyName("disclaimerAccepted")]
    public bool DisclaimerAccepted { get; set; }

    // ----- desktop overlay (off unless the user turns it on) -----

    [JsonPropertyName("overlayEnabled")]
    public bool OverlayEnabled { get; set; }

    [JsonPropertyName("overlayCompact")]
    public bool OverlayCompact { get; set; } = true;

    /// <summary>Locked = click-through; the overlay ignores the mouse and can only be unlocked from the tray menu.</summary>
    [JsonPropertyName("overlayLocked")]
    public bool OverlayLocked { get; set; }

    [JsonPropertyName("overlayOpacity")]
    public int OverlayOpacity { get; set; } = AppConstants.DefaultOverlayOpacity;

    /// <summary>Null until the user first drags the overlay; then the default corner is used.</summary>
    [JsonPropertyName("overlayX")]
    public int? OverlayX { get; set; }

    [JsonPropertyName("overlayY")]
    public int? OverlayY { get; set; }

    public void Normalize()
    {
        OverlayOpacity = Math.Clamp(OverlayOpacity, AppConstants.MinOverlayOpacity, AppConstants.MaxOverlayOpacity);
        QuietStartHour = Math.Clamp(QuietStartHour, 0, 23);
        QuietEndHour = Math.Clamp(QuietEndHour, 0, 23);
        if (!Enum.IsDefined(PollingMode)) PollingMode = PollingMode.Balanced;
    }
}
