using System.Text.Json;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Services;

public sealed record UpdateInfo(Version Version, string Tag, Uri ReleasePage);

/// <summary>
/// Asks GitHub, at most once a day, whether a newer release exists. It only tells the user (a tray menu item that
/// opens the release page); it never downloads or installs anything. No credential or usage data is sent.
/// </summary>
public sealed class UpdateChecker(HttpMessageHandler? handler = null) : IDisposable
{
    public const string Repository = "charitty-ccrp/ClaudeUsageTray";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly string ReleasesPrefix = $"https://github.com/{Repository}/releases/";

    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 512 * 1024,
    };

    public static Version CurrentVersion { get; } =
        typeof(UpdateChecker).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>The newer release, or null when up to date, offline, or GitHub answered anything unexpected.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("User-Agent", $"ClaudeUsageTray/{current}");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(json, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Logger.Info($"Update check skipped: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>Pure: reads tag_name / html_url from GitHub's "latest release" JSON.</summary>
    public static UpdateInfo? Parse(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;

            var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var page = root.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (ParseTag(tag) is not { } version || version <= current) return null;

            // only ever open this repository's own release pages
            if (page is null || !page.StartsWith(ReleasesPrefix, StringComparison.Ordinal) ||
                !Uri.TryCreate(page, UriKind.Absolute, out var uri))
                return null;
            return new UpdateInfo(version, tag!, uri);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Version? ParseTag(string? tag)
    {
        var text = tag?.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(text) || !Version.TryParse(text, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public void Dispose() => _http.Dispose();
}
