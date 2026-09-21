using System.Net;
using System.Text;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;

namespace ClaudeUsageTray.Tests;

internal sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return respond(request);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
}

public class ClaudeUsageParsingTests
{
    [Fact]
    public void Parses_five_hour_and_optional_windows()
    {
        const string json = """
        {
          "five_hour": { "utilization": 63.0, "resets_at": "2026-09-19T04:00:00Z" },
          "seven_day": { "utilization": 11.0, "resets_at": "2026-09-25T16:59:59Z" },
          "seven_day_sonnet": { "utilization": 0.0, "resets_at": null },
          "seven_day_opus": null,
          "extra_usage": null,
          "some_future_field": { "x": 1 }
        }
        """;

        var result = ClaudeUsageClient.ParseUsage(json);

        Assert.True(result.IsSuccess);
        Assert.Equal(63.0, result.Usage!.FiveHour.Utilization);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 4, 0, 0, TimeSpan.Zero), result.Usage.FiveHour.ResetsAt);
        Assert.Equal(11.0, result.Usage.SevenDay!.Utilization);
        Assert.Null(result.Usage.SevenDaySonnet!.ResetsAt);
        Assert.Null(result.Usage.SevenDayOpus);
    }

    [Theory]
    [InlineData("""{ "five_hour": { "utilization": 0 } }""", 0)]
    [InlineData("""{ "five_hour": { "utilization": 100 } }""", 100)]
    [InlineData("""{ "five_hour": { "utilization": 17.5 } }""", 17.5)]
    public void Accepts_boundary_utilization(string json, double expected)
    {
        var result = ClaudeUsageClient.ParseUsage(json);
        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Usage!.FiveHour.Utilization);
    }

    [Fact]
    public void Missing_resets_at_still_shows_usage()
    {
        var result = ClaudeUsageClient.ParseUsage("""{ "five_hour": { "utilization": 40 } }""");
        Assert.True(result.IsSuccess);
        Assert.Null(result.Usage!.FiveHour.ResetsAt);
    }

    [Fact]
    public void Garbage_resets_at_is_treated_as_unavailable()
    {
        var result = ClaudeUsageClient.ParseUsage("""{ "five_hour": { "utilization": 40, "resets_at": "not a date" } }""");
        Assert.True(result.IsSuccess);
        Assert.Null(result.Usage!.FiveHour.ResetsAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "five_hour": null }""")]
    [InlineData("""{ "five_hour": {} }""")]
    [InlineData("""{ "five_hour": { "utilization": "63" } }""")]
    [InlineData("""{ "five_hour": { "utilization": -1 } }""")]
    [InlineData("""{ "five_hour": { "utilization": 150 } }""")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void Invalid_shapes_are_rejected_never_assumed_zero(string json)
    {
        var result = ClaudeUsageClient.ParseUsage(json);
        Assert.False(result.IsSuccess);
        Assert.Equal(UsageErrorKind.InvalidResponse, result.Error);
        Assert.Null(result.Usage);
    }

    [Theory]
    [InlineData("sk-ant-sid01-abcdefghijklmnop", "sk-ant-sid01-abcdefghijklmnop")]
    [InlineData("  sessionKey=sk-ant-sid01-abcdefghijklmnop;  ", "sk-ant-sid01-abcdefghijklmnop")]
    [InlineData("\"sk-ant-sid01-abcdefghijklmnop\"", "sk-ant-sid01-abcdefghijklmnop")]
    public void Normalizes_pasted_session_keys(string raw, string expected) =>
        Assert.Equal(expected, ClaudeUsageClient.NormalizeSessionKey(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has space in it 1234567890")]
    [InlineData("bad\r\nheader: injection-1234567890")]
    [InlineData("semi;colon-injection-1234567890")]
    public void Rejects_malformed_session_keys(string? raw) =>
        Assert.Null(ClaudeUsageClient.NormalizeSessionKey(raw));

    [Fact]
    public void Organization_id_must_be_a_guid()
    {
        Assert.Equal("123e4567-e89b-12d3-a456-426614174000",
            ClaudeUsageClient.NormalizeOrganizationId(" 123E4567-E89B-12D3-A456-426614174000 "));
        Assert.Null(ClaudeUsageClient.NormalizeOrganizationId("../../etc"));
        Assert.Null(ClaudeUsageClient.NormalizeOrganizationId(null));
    }

    [Fact]
    public void Parses_organization_list_and_skips_bad_entries()
    {
        const string json = """
        [
          { "uuid": "123e4567-e89b-12d3-a456-426614174000", "name": "Personal" },
          { "id": "223e4567-e89b-12d3-a456-426614174000" },
          { "uuid": "nope", "name": "Bad" },
          "junk"
        ]
        """;

        var orgs = ClaudeUsageClient.ParseOrganizations(json);

        Assert.Equal(2, orgs.Count);
        Assert.Equal("Personal", orgs[0].Name);
        Assert.Null(orgs[1].Name);
    }

    [Theory]
    [InlineData("""{ "memberships": [ { "organization": { "raw_plan_type": "claude_pro" } } ] }""", "Pro")]
    [InlineData("""{ "plan": "max_5x" }""", "Max 5x")]
    [InlineData("""{ "name": "no plan here" }""", null)]
    [InlineData("nonsense", null)]
    public void Extracts_plan_name_best_effort(string json, string? expected) =>
        Assert.Equal(expected, ClaudeUsageClient.ExtractPlanName(json));
}

public class ClaudeUsageClientHttpTests
{
    private const string Key = "sk-ant-sid01-abcdefghijklmnopqrstuvwxyz";
    private const string Org = "123e4567-e89b-12d3-a456-426614174000";
    private const string ValidJson = """{ "five_hour": { "utilization": 63, "resets_at": "2026-09-19T04:00:00Z" } }""";

    private static ClaudeUsageClient Create(FakeHandler handler, string? key = Key, string? org = Org) =>
        new(() => key, () => org, handler);

    [Fact]
    public async Task Valid_response_returns_usage_and_sends_cookie_to_claude_only()
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(ValidJson)));
        using var client = Create(handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("claude.ai", request.RequestUri!.Host);
        Assert.Equal(Uri.UriSchemeHttps, request.RequestUri.Scheme);
        Assert.Equal($"/api/organizations/{Org}/usage", request.RequestUri.AbsolutePath);
        Assert.Equal($"sessionKey={Key}", request.Headers.GetValues("Cookie").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UsageErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, UsageErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError, UsageErrorKind.Server)]
    [InlineData(HttpStatusCode.BadGateway, UsageErrorKind.Server)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UsageErrorKind.Server)]
    [InlineData(HttpStatusCode.GatewayTimeout, UsageErrorKind.Server)]
    [InlineData(HttpStatusCode.TooManyRequests, UsageErrorKind.Server)]
    [InlineData(HttpStatusCode.NotFound, UsageErrorKind.InvalidResponse)]
    [InlineData(HttpStatusCode.Redirect, UsageErrorKind.InvalidResponse)]
    public async Task Http_status_maps_to_error_kind(HttpStatusCode status, UsageErrorKind expected)
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("{}", status)));
        using var client = Create(handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error);
        Assert.DoesNotContain(Key, result.Message ?? string.Empty);
    }

    [Fact]
    public async Task Cloudflare_challenge_403_is_reported_as_such_and_stays_forbidden()
    {
        var handler = new FakeHandler(_ => Task.FromResult(
            FakeHandler.Json("<html><title>Just a moment...</title></html>", HttpStatusCode.Forbidden, "text/html")));
        using var client = Create(handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(UsageErrorKind.Forbidden, result.Error);
        Assert.Contains("Cloudflare", result.Message);
        Assert.DoesNotContain(Key, result.Message);
    }

    [Fact]
    public async Task Plain_403_is_not_labelled_cloudflare()
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("{\"error\":\"denied\"}", HttpStatusCode.Forbidden)));
        using var client = Create(handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.DoesNotContain("Cloudflare", result.Message);
    }

    [Fact]
    public async Task Html_challenge_page_is_an_invalid_response_not_a_crash()
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("<html>challenge</html>", mediaType: "text/html")));
        using var client = Create(handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(UsageErrorKind.InvalidResponse, result.Error);
    }

    [Fact]
    public async Task Malformed_json_is_an_invalid_response()
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("{ not json")));
        using var client = Create(handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(UsageErrorKind.InvalidResponse, result.Error);
    }

    [Fact]
    public async Task Timeout_and_network_failures_map_to_network()
    {
        using var timeout = Create(new FakeHandler(_ => throw new TaskCanceledException("timeout")));
        using var network = Create(new FakeHandler(_ => throw new HttpRequestException("boom")));

        Assert.Equal(UsageErrorKind.Network, (await timeout.GetUsageAsync(CancellationToken.None)).Error);
        Assert.Equal(UsageErrorKind.Network, (await network.GetUsageAsync(CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_network_error()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var client = Create(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetUsageAsync(cts.Token));
    }

    [Fact]
    public async Task Missing_credential_or_org_makes_no_request()
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(ValidJson)));
        using var noKey = Create(handler, key: null);
        using var badOrg = Create(handler, org: "../evil");

        Assert.Equal(UsageErrorKind.NotConfigured, (await noKey.GetUsageAsync(CancellationToken.None)).Error);
        Assert.Equal(UsageErrorKind.NotConfigured, (await badOrg.GetUsageAsync(CancellationToken.None)).Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Organization_discovery_returns_ids()
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json($$"""[ { "uuid": "{{Org}}", "name": "Me" } ]""")));
        using var client = Create(handler);

        var result = await client.DiscoverOrganizationsAsync(Key, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Org, Assert.Single(result.Organizations).Id);
        Assert.Equal("/api/organizations", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Plan_name_lookup_failure_returns_null()
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("{}", HttpStatusCode.Forbidden)));
        using var client = Create(handler);

        Assert.Null(await client.GetPlanNameAsync(CancellationToken.None));
    }
}
