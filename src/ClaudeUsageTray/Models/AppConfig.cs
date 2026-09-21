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

    [JsonPropertyName("refreshIntervalSeconds")]
    public int RefreshIntervalSeconds { get; set; } = AppConstants.DefaultRefreshSeconds;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    /// <summary>Date the credential was saved (not the key itself), used to warn before the ~30 day expiry.</summary>
    [JsonPropertyName("credentialSavedAt")]
    public DateTimeOffset? CredentialSavedAt { get; set; }

    [JsonPropertyName("notificationsEnabled")]
    public bool NotificationsEnabled { get; set; } = true;

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
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, AppConstants.MinRefreshSeconds, AppConstants.MaxRefreshSeconds);
        OverlayOpacity = Math.Clamp(OverlayOpacity, AppConstants.MinOverlayOpacity, AppConstants.MaxOverlayOpacity);
    }
}
