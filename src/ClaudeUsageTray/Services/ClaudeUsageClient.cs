using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeUsageTray.App;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Services;

public sealed record OrganizationInfo(string Id, string? Name);

public sealed record OrganizationDiscoveryResult(
    IReadOnlyList<OrganizationInfo> Organizations, UsageErrorKind Error, string? Message)
{
    public bool IsSuccess => Error == UsageErrorKind.None;
}

/// <summary>
/// The ONLY class that knows Claude's URLs and JSON. If claude.ai changes, fix it here.
/// Uses an undocumented web endpoint; never bypasses Cloudflare; never logs the credential.
/// </summary>
public sealed partial class ClaudeUsageClient : IUsageProvider, IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    private readonly Func<string?> _sessionKey;
    private readonly Func<string?> _organizationId;
    private readonly HttpClient _http;

    public ClaudeUsageClient(Func<string?> sessionKey, Func<string?> organizationId, HttpMessageHandler? handler = null)
    {
        _sessionKey = sessionKey;
        _organizationId = organizationId;
        _http = new HttpClient(handler ?? ClaudeHttp.CreateDefaultHandler())
        {
            Timeout = AppConstants.HttpTimeout,
            MaxResponseContentBufferSize = 1024 * 1024,
        };
    }

    // ---------- IUsageProvider ----------

    public Task<UsageFetchResult> GetUsageAsync(CancellationToken cancellationToken) =>
        FetchUsageAsync(_sessionKey(), _organizationId(), cancellationToken);

    public async Task<string?> GetPlanNameAsync(CancellationToken cancellationToken)
    {
        var key = NormalizeSessionKey(_sessionKey());
        if (key is null) return null;
        var raw = await GetJsonAsync("/api/account", key, cancellationToken).ConfigureAwait(false);
        return raw.Error == UsageErrorKind.None && raw.Body is not null ? ExtractPlanName(raw.Body) : null;
    }

    // ---------- Public operations (also used by SetupForm "Test Connection") ----------

    public async Task<UsageFetchResult> FetchUsageAsync(string? sessionKey, string? organizationId, CancellationToken cancellationToken)
    {
        var key = NormalizeSessionKey(sessionKey);
        if (key is null)
            return UsageFetchResult.Fail(UsageErrorKind.NotConfigured, "Session credential is missing or has an invalid format.");

        var org = NormalizeOrganizationId(organizationId);
        if (org is null)
            return UsageFetchResult.Fail(UsageErrorKind.NotConfigured, "Organization ID is missing or invalid.");

        var raw = await GetJsonAsync($"/api/organizations/{org}/usage", key, cancellationToken).ConfigureAwait(false);
        if (raw.Error != UsageErrorKind.None || raw.Body is null)
            return UsageFetchResult.Fail(raw.Error, raw.Message ?? "Request failed.", raw.RetryAfter);

        return ParseUsage(raw.Body);
    }

    public async Task<OrganizationDiscoveryResult> DiscoverOrganizationsAsync(string? sessionKey, CancellationToken cancellationToken)
    {
        var key = NormalizeSessionKey(sessionKey);
        if (key is null)
            return new OrganizationDiscoveryResult([], UsageErrorKind.NotConfigured, "Session credential is missing or has an invalid format.");

        var raw = await GetJsonAsync("/api/organizations", key, cancellationToken).ConfigureAwait(false);
        if (raw.Error != UsageErrorKind.None || raw.Body is null)
            return new OrganizationDiscoveryResult([], raw.Error, raw.Message ?? "Request failed.");

        var orgs = ParseOrganizations(raw.Body);
        return orgs.Count == 0
            ? new OrganizationDiscoveryResult([], UsageErrorKind.InvalidResponse,
                $"No organization IDs found in the response ({DescribeShape(raw.Body)}).")
            : new OrganizationDiscoveryResult(orgs, UsageErrorKind.None, null);
    }

    /// <summary>Structure only (types and property NAMES, never values) so it is safe to show and log.</summary>
    public static string DescribeShape(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Array:
                    var first = root.EnumerateArray().FirstOrDefault();
                    return first.ValueKind == JsonValueKind.Object
                        ? $"array of {root.GetArrayLength()}, item fields: {string.Join(", ", first.EnumerateObject().Select(p => p.Name).Take(12))}"
                        : $"array of {root.GetArrayLength()}";
                case JsonValueKind.Object:
                    return $"object, fields: {string.Join(", ", root.EnumerateObject().Select(p => p.Name).Take(12))}";
                default:
                    return root.ValueKind.ToString();
            }
        }
        catch (JsonException)
        {
            return "not JSON";
        }
    }

    // ---------- Input normalisation / validation ----------

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.%=~+/]{16,4096}$")]
    private static partial Regex KeyShape();

    /// <summary>Accepts what users paste from DevTools ("sessionKey=..." / quoted / trailing ';') and validates the shape.</summary>
    public static string? NormalizeSessionKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().Trim('"', '\'');
        if (value.StartsWith("sessionKey=", StringComparison.OrdinalIgnoreCase))
            value = value["sessionKey=".Length..];
        value = value.TrimEnd(';').Trim().Trim('"', '\'');
        return KeyShape().IsMatch(value) ? value : null;
    }

    /// <summary>Organization IDs are GUIDs; enforcing that also prevents path injection into the URL.</summary>
    public static string? NormalizeOrganizationId(string? raw) =>
        Guid.TryParse(raw?.Trim(), out var guid) ? guid.ToString("D") : null;

    // ---------- HTTP ----------

    private Task<RawResponse> GetJsonAsync(string path, string sessionKey, CancellationToken cancellationToken) =>
        ClaudeHttp.GetJsonAsync(
            _http,
            new Uri("https://" + AppConstants.ClaudeHost + path),
            AppConstants.ClaudeHost,
            request =>
            {
                request.Headers.TryAddWithoutValidation("Cookie", "sessionKey=" + sessionKey);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            },
            cancellationToken);

    // ---------- Parsing (pure, unit-tested) ----------

    public static UsageFetchResult ParseUsage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Invalid("Unexpected response format.");

            var root = doc.RootElement;
            if (!root.TryGetProperty("five_hour", out var fiveHourElement) || fiveHourElement.ValueKind != JsonValueKind.Object)
                return Invalid("Usage response has no five_hour window.");

            var fiveHour = ParseEntry(fiveHourElement);
            if (fiveHour is null)
                return Invalid("five_hour.utilization is missing or out of range.");

            var usage = new UsageResponse(
                fiveHour,
                ParseOptionalEntry(root, "seven_day"),
                ParseOptionalEntry(root, "seven_day_opus"),
                ParseOptionalEntry(root, "seven_day_sonnet"));
            return UsageFetchResult.Ok(usage);
        }
        catch (JsonException)
        {
            return Invalid("Unexpected response format.");
        }

        static UsageFetchResult Invalid(string message) => UsageFetchResult.Fail(UsageErrorKind.InvalidResponse, message);
    }

    private static UsageEntry? ParseOptionalEntry(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Object ? ParseEntry(element) : null;

    private static UsageEntry? ParseEntry(JsonElement element)
    {
        if (!element.TryGetProperty("utilization", out var util) || util.ValueKind != JsonValueKind.Number)
            return null;
        var value = util.GetDouble();
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 100)
            return null;

        DateTimeOffset? resetsAt = null;
        if (element.TryGetProperty("resets_at", out var reset) && reset.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            resetsAt = parsed;
        }
        return new UsageEntry(value, resetsAt);
    }

    public static IReadOnlyList<OrganizationInfo> ParseOrganizations(string json)
    {
        var result = new List<OrganizationInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(item, "uuid") ?? ReadString(item, "id");
                var normalized = NormalizeOrganizationId(id);
                if (normalized is null) continue;
                result.Add(new OrganizationInfo(normalized, ReadString(item, "name")));
            }
        }
        catch (JsonException)
        {
            result.Clear();
        }
        return result;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static readonly string[] PlanFieldNames = ["raw_plan_type", "plan_type", "subscription_type", "plan"];

    /// <summary>Best effort: the /api/account shape is unverified, so search a few likely field names.</summary>
    public static string? ExtractPlanName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var raw = FindPlan(doc.RootElement, 0);
            return raw is null ? null : FormatPlanName(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindPlan(JsonElement element, int depth)
    {
        if (depth > 6) return null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        PlanFieldNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(property.Value.GetString()))
                        return property.Value.GetString();
                }
                foreach (var property in element.EnumerateObject())
                {
                    var nested = FindPlan(property.Value, depth + 1);
                    if (nested is not null) return nested;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindPlan(item, depth + 1);
                    if (nested is not null) return nested;
                }
                return null;
            default:
                return null;
        }
    }

    public static string? FormatPlanName(string raw)
    {
        var text = raw.Trim().ToLowerInvariant();
        if (text.StartsWith("claude_") || text.StartsWith("claude-")) text = text[7..];
        var words = text.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        var formatted = string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return formatted.Length > 20 ? formatted[..20] : formatted;
    }

    public void Dispose() => _http.Dispose();
}
