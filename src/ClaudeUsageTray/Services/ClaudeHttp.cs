using System.Net;
using ClaudeUsageTray.Models;

namespace ClaudeUsageTray.Services;

internal sealed record RawResponse(UsageErrorKind Error, string? Message, string? Body, TimeSpan? RetryAfter = null);

/// <summary>
/// Shared GET-JSON plumbing for both providers: host allow-listing, status mapping, content-type check.
/// Never follows redirects (the handler disables them) and never puts credentials in messages.
/// </summary>
internal static class ClaudeHttp
{
    /// <summary>Pause before the single retry of a transient failure. Tests set it to zero.</summary>
    internal static TimeSpan TransientRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public static async Task<RawResponse> GetJsonAsync(
        HttpClient http, Uri uri, string expectedHost, Action<HttpRequestMessage> addCredential, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to send credential to an unexpected host.");

        // one quiet retry for blips (dropped connection, timeout, gateway error) before reporting a failure
        var result = await SendOnceAsync(http, uri, addCredential, cancellationToken).ConfigureAwait(false);
        if (!result.Retryable) return result.Response;

        await Task.Delay(TransientRetryDelay, cancellationToken).ConfigureAwait(false);
        return (await SendOnceAsync(http, uri, addCredential, cancellationToken).ConfigureAwait(false)).Response;
    }

    private static async Task<(RawResponse Response, bool Retryable)> SendOnceAsync(
        HttpClient http, Uri uri, Action<HttpRequestMessage> addCredential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        addCredential(request);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var code = (int)response.StatusCode;

            if (code is >= 300 and < 400)
                return (new RawResponse(UsageErrorKind.InvalidResponse, "Unexpected redirect.", null), false);
            if (code == 401)
                return (new RawResponse(UsageErrorKind.Authentication, "Authentication required (HTTP 401).", null), false);
            if (code == 403)
            {
                var challenged = await IsCloudflareChallengeAsync(response, cancellationToken).ConfigureAwait(false);
                return (new RawResponse(UsageErrorKind.Forbidden, challenged
                    ? "Blocked by a Cloudflare challenge (HTTP 403). The credential may still be valid; this app does not bypass the challenge."
                    : "Request rejected or access changed (HTTP 403).", null), false);
            }
            if (code == 429)
                return (new RawResponse(UsageErrorKind.RateLimited, "Too many requests (HTTP 429).", null,
                    RetryAfterOf(response, DateTimeOffset.UtcNow)), false);
            if (code >= 500)
                return (new RawResponse(UsageErrorKind.Server, $"Service temporarily unavailable (HTTP {code}).", null),
                    code is 502 or 503 or 504);
            if (!response.IsSuccessStatusCode)
                return (new RawResponse(UsageErrorKind.InvalidResponse, $"Unexpected response (HTTP {code}).", null), false);

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
                return (new RawResponse(UsageErrorKind.InvalidResponse, "Unexpected response format.", null), false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (new RawResponse(UsageErrorKind.None, null, body), false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (new RawResponse(UsageErrorKind.Network, "Request timed out.", null), true);
        }
        catch (HttpRequestException)
        {
            return (new RawResponse(UsageErrorKind.Network, "Network unavailable.", null), true);
        }
    }

    /// <summary>Reads Retry-After as seconds or an HTTP date; null when absent or already past.</summary>
    internal static TimeSpan? RetryAfterOf(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta > TimeSpan.Zero ? delta : null;
        if (header?.Date is { } date) return date > now ? date - now : null;
        return null;
    }

    /// <summary>Diagnostic only: recognises a Cloudflare interstitial so the user gets an accurate message.</summary>
    private static async Task<bool> IsCloudflareChallengeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Headers.TryGetValues("cf-mitigated", out var mitigated) &&
            mitigated.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
            return true;

        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("cf-chl", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return false;
        }
    }

    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,                       // never forward a credential to another host
        UseCookies = false,                              // credentials are set explicitly per request
        AutomaticDecompression = DecompressionMethods.All,
        DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials,
        ConnectTimeout = TimeSpan.FromSeconds(5),        // fail fast on a dead network instead of using the whole timeout
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),   // pick up DNS / network changes
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60), // never reuse a socket that died during sleep
    };
}
