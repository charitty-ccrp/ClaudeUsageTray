using System.Net;
using ClaudeUsageTray.Models;
using ClaudeUsageTray.Services;
using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Tests;

public class OAuthCredentialParsingTests
{
    [Fact]
    public void Parses_nested_claudeAiOauth_with_epoch_milliseconds()
    {
        const string json = """
        { "claudeAiOauth": { "accessToken": "sk-ant-oat01-abc", "refreshToken": "sk-ant-ort01-zzz",
          "expiresAt": 1789000000000, "scopes": ["user:inference"], "subscriptionType": "pro" } }
        """;

        var credential = ClaudeCodeOAuthClient.ParseCredential(json);

        Assert.NotNull(credential);
        Assert.Equal("sk-ant-oat01-abc", credential!.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789000000000), credential.ExpiresAt);
        Assert.Equal("pro", credential.SubscriptionType);
    }

    [Fact]
    public void Accepts_fields_at_the_root_and_seconds_timestamps()
    {
        var credential = ClaudeCodeOAuthClient.ParseCredential("""{ "accessToken": "tok-123", "expiresAt": 1789000000 }""");

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789000000), credential!.ExpiresAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "claudeAiOauth": {} }""")]
    [InlineData("""{ "claudeAiOauth": { "accessToken": "" } }""")]
    [InlineData("""{ "claudeAiOauth": { "accessToken": 42 } }""")]
    [InlineData("""{ "claudeAiOauth": { "accessToken": "has space" } }""")]
    [InlineData("""{ "claudeAiOauth": { "accessToken": "bad\r\nheader" } }""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Rejects_unusable_credential_files(string json) =>
        Assert.Null(ClaudeCodeOAuthClient.ParseCredential(json));
}

public class ClaudeCodeOAuthClientTests
{
    private const string Token = "sk-ant-oat01-SECRETTOKENVALUE123";
    private const string ValidJson = """{ "five_hour": { "utilization": 42.5, "resets_at": "2026-09-19T04:00:00Z" } }""";
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 2, 0, 0, TimeSpan.Zero);

    private static string WriteCredentials(TempDir dir, DateTimeOffset expiresAt, string token = Token, string? subscription = "max")
    {
        var path = Path.Combine(dir.Path, ".credentials.json");
        var sub = subscription is null ? string.Empty : $", \"subscriptionType\": \"{subscription}\"";
        File.WriteAllText(path,
            $$"""{ "claudeAiOauth": { "accessToken": "{{token}}", "expiresAt": {{expiresAt.ToUnixTimeMilliseconds()}}{{sub}} } }""");
        return path;
    }

    private static ClaudeCodeOAuthClient Create(string path, FakeHandler handler) => new(path, handler, () => Now);

    [Fact]
    public async Task Sends_bearer_token_and_beta_header_to_anthropic_api_only()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(ValidJson)));
        using var client = Create(WriteCredentials(dir, Now.AddHours(1)), handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(42.5, result.Usage!.FiveHour.Utilization);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("api.anthropic.com", request.RequestUri!.Host);
        Assert.Equal("/api/oauth/usage", request.RequestUri.AbsolutePath);
        Assert.Equal($"Bearer {Token}", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta").Single());
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task Reads_the_token_fresh_on_every_call_so_claude_codes_refresh_is_picked_up()
    {
        using var dir = new TempDir();
        var path = WriteCredentials(dir, Now.AddHours(1), token: "sk-ant-oat01-FIRST");
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(ValidJson)));
        using var client = Create(path, handler);

        await client.GetUsageAsync(CancellationToken.None);
        WriteCredentials(dir, Now.AddHours(2), token: "sk-ant-oat01-SECOND");
        await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal("Bearer sk-ant-oat01-FIRST", handler.Requests[0].Headers.GetValues("Authorization").Single());
        Assert.Equal("Bearer sk-ant-oat01-SECOND", handler.Requests[1].Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Expired_token_makes_no_request_and_is_retryable()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(ValidJson)));
        using var client = Create(WriteCredentials(dir, Now.AddMinutes(-1)), handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(UsageErrorKind.CredentialUnavailable, result.Error);
        Assert.Empty(handler.Requests);
        Assert.NotNull(BackoffPolicy.NextDelay(result.Error, 1, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task Missing_or_corrupt_file_is_credential_unavailable_without_a_request()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(ValidJson)));

        using (var missing = Create(Path.Combine(dir.Path, "nope.json"), handler))
            Assert.Equal(UsageErrorKind.CredentialUnavailable, (await missing.GetUsageAsync(CancellationToken.None)).Error);

        var corrupt = Path.Combine(dir.Path, "corrupt.json");
        File.WriteAllText(corrupt, "{ nope");
        using (var bad = Create(corrupt, handler))
            Assert.Equal(UsageErrorKind.CredentialUnavailable, (await bad.GetUsageAsync(CancellationToken.None)).Error);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unauthorized_is_retryable_credential_problem_not_a_permanent_auth_failure()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("{}", HttpStatusCode.Unauthorized)));
        using var client = Create(WriteCredentials(dir, Now.AddHours(1)), handler);

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(UsageErrorKind.CredentialUnavailable, result.Error);
        Assert.NotNull(BackoffPolicy.NextDelay(result.Error, 1, TimeSpan.FromSeconds(60)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, UsageErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, UsageErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, UsageErrorKind.Server)]
    public async Task Other_statuses_map_like_the_web_client(HttpStatusCode status, UsageErrorKind expected)
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("{}", status)));
        using var client = Create(WriteCredentials(dir, Now.AddHours(1)), handler);

        Assert.Equal(expected, (await client.GetUsageAsync(CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Token_never_appears_in_any_result_message()
    {
        using var dir = new TempDir();
        var path = WriteCredentials(dir, Now.AddHours(1));

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError, HttpStatusCode.NotFound })
        {
            var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("{}", status)));
            using var client = Create(path, handler);
            var result = await client.GetUsageAsync(CancellationToken.None);
            Assert.DoesNotContain("SECRETTOKENVALUE", result.Message);
        }
    }

    [Fact]
    public async Task Network_failure_maps_to_network()
    {
        using var dir = new TempDir();
        using var client = Create(WriteCredentials(dir, Now.AddHours(1)), new FakeHandler(_ => throw new HttpRequestException("boom")));

        Assert.Equal(UsageErrorKind.Network, (await client.GetUsageAsync(CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Plan_name_comes_from_the_subscription_type_without_any_request()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(ValidJson)));
        using var client = Create(WriteCredentials(dir, Now.AddHours(1), subscription: "max"), handler);

        Assert.Equal("Max", await client.GetPlanNameAsync(CancellationToken.None));
        Assert.Empty(handler.Requests);
    }
}

public class RoutingAndConfigTests
{
    private sealed class Tagged(string tag) : IUsageProvider
    {
        public Task<UsageFetchResult> GetUsageAsync(CancellationToken cancellationToken) =>
            Task.FromResult(UsageFetchResult.Fail(UsageErrorKind.Network, tag));

        public Task<string?> GetPlanNameAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(tag);
    }

    [Fact]
    public async Task Router_follows_the_current_mode_on_every_call()
    {
        var mode = AuthMode.WebCookie;
        var router = new RoutingUsageProvider(() => mode, new Tagged("web"), new Tagged("oauth"));

        Assert.Equal("web", (await router.GetUsageAsync(CancellationToken.None)).Message);
        mode = AuthMode.ClaudeCodeOAuth;
        Assert.Equal("oauth", (await router.GetUsageAsync(CancellationToken.None)).Message);
        Assert.Equal("oauth", await router.GetPlanNameAsync(CancellationToken.None));
    }

    [Fact]
    public void Auth_mode_round_trips_as_a_readable_string_and_defaults_to_web_cookie()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        var store = new ConfigStore(path);

        Assert.Equal(AuthMode.WebCookie, store.Load().AuthMode);
        store.Save(new AppConfig { AuthMode = AuthMode.ClaudeCodeOAuth });

        Assert.Contains("ClaudeCodeOAuth", File.ReadAllText(path));
        Assert.Equal(AuthMode.ClaudeCodeOAuth, store.Load().AuthMode);
    }

    [Fact]
    public void Logger_redacts_bearer_tokens_and_oauth_keys()
    {
        var text = Logger.Redact("Authorization: Bearer sk-ant-oat01-SECRET and sk-ant-ort01-OTHER");

        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("OTHER", text);
    }
}
