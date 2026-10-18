using System.Text.Json;
using ClaudeUsageTray.App;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Services;

public sealed record OAuthCredential(string AccessToken, DateTimeOffset? ExpiresAt, string? SubscriptionType);

/// <summary>
/// Reads the access token Claude Code keeps in ~/.claude/.credentials.json and calls the undocumented
/// api.anthropic.com OAuth usage endpoint. The token is read per request and never cached, copied, logged or
/// refreshed here: refreshing would rotate the refresh token and could sign Claude Code out.
/// </summary>
public sealed class ClaudeCodeOAuthClient : IUsageProvider, IDisposable
{
    public const string Host = "api.anthropic.com";
    private const string UsagePath = "/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";

    private readonly string _credentialsPath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HttpClient _http;

    public ClaudeCodeOAuthClient(string? credentialsPath = null, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        _credentialsPath = credentialsPath ?? DefaultCredentialsPath;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _http = new HttpClient(handler ?? ClaudeHttp.CreateDefaultHandler())
        {
            Timeout = AppConstants.HttpTimeout,
            MaxResponseContentBufferSize = 1024 * 1024,
        };
    }

    public static string DefaultCredentialsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public async Task<UsageFetchResult> GetUsageAsync(CancellationToken cancellationToken)
    {
        var read = ReadCredential();
        if (read.Credential is not { } credential)
            return UsageFetchResult.Fail(UsageErrorKind.CredentialUnavailable, read.Problem!);

        if (credential.ExpiresAt is { } expiresAt && expiresAt <= _clock())
            return UsageFetchResult.Fail(UsageErrorKind.CredentialUnavailable,
                "Claude Code's login token has expired. Use Claude Code once so it refreshes the token; this app retries automatically.");

        var raw = await ClaudeHttp.GetJsonAsync(
            _http,
            new Uri("https://" + Host + UsagePath),
            Host,
            request =>
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential.AccessToken);
                request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
                request.Headers.TryAddWithoutValidation("User-Agent", "ClaudeUsageTray/" + AppVersion);
            },
            cancellationToken).ConfigureAwait(false);

        // A 401 here almost always means the short-lived token expired; Claude Code renews it on its own, so keep retrying.
        if (raw.Error == UsageErrorKind.Authentication)
            return UsageFetchResult.Fail(UsageErrorKind.CredentialUnavailable,
                "Claude Code's token was rejected (HTTP 401). Use Claude Code once so it refreshes the token; this app retries automatically.");
        if (raw.Error != UsageErrorKind.None || raw.Body is null)
            return UsageFetchResult.Fail(raw.Error, raw.Message ?? "Request failed.", raw.RetryAfter);

        return ClaudeUsageClient.ParseUsage(raw.Body);
    }

    public Task<string?> GetPlanNameAsync(CancellationToken cancellationToken)
    {
        var subscription = ReadCredential().Credential?.SubscriptionType;
        return Task.FromResult(string.IsNullOrWhiteSpace(subscription) ? null : ClaudeUsageClient.FormatPlanName(subscription));
    }

    private static string AppVersion =>
        typeof(ClaudeCodeOAuthClient).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private (OAuthCredential? Credential, string? Problem) ReadCredential()
    {
        try
        {
            if (!File.Exists(_credentialsPath))
                return (null, "Claude Code login not found (~/.claude/.credentials.json). Sign in to Claude Code first.");

            // Claude Code may be rewriting the file while we read it, so allow shared access.
            using var stream = new FileStream(_credentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var credential = ParseCredential(reader.ReadToEnd());
            return credential is null
                ? (null, "Claude Code login file has no OAuth access token (it may use another login method).")
                : (credential, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not read Claude Code credentials: {ex.GetType().Name}");
            return (null, "Could not read the Claude Code login file.");
        }
    }

    /// <summary>Tolerant parse: expects { "claudeAiOauth": { accessToken, expiresAt, subscriptionType } } but also accepts the fields at the root.</summary>
    public static OAuthCredential? ParseCredential(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var container = root.TryGetProperty("claudeAiOauth", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            if (!container.TryGetProperty("accessToken", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
                return null;
            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl) || token.Any(char.IsWhiteSpace))
                return null;

            DateTimeOffset? expiresAt = null;
            if (container.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number &&
                expires.TryGetInt64(out var value) && value > 0)
            {
                // Claude Code writes epoch milliseconds; tolerate seconds too.
                expiresAt = value > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
            }

            var subscription = container.TryGetProperty("subscriptionType", out var sub) && sub.ValueKind == JsonValueKind.String
                ? sub.GetString()
                : null;

            return new OAuthCredential(token, expiresAt, subscription);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
