using System.Net;
using ClaudeUsageTray.Services;

namespace ClaudeUsageTray.Tests;

public class UpdateCheckerTests
{
    private static readonly Version Current = new(0, 2, 0);

    private static string Release(string tag, string url = "https://github.com/charitty-ccrp/ClaudeUsageTray/releases/tag/v0.3.0",
        bool prerelease = false) =>
        $$"""{ "tag_name": "{{tag}}", "html_url": "{{url}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}} }""";

    [Fact]
    public void A_newer_tag_is_reported_with_its_release_page()
    {
        var update = UpdateChecker.Parse(Release("v0.3.0"), Current);

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 3, 0), update.Version);
        Assert.Equal("v0.3.0", update.Tag);
    }

    [Theory]
    [InlineData("v0.2.0")]
    [InlineData("0.1.9")]
    [InlineData("not-a-version")]
    [InlineData("")]
    public void Same_older_or_unreadable_tags_are_ignored(string tag) =>
        Assert.Null(UpdateChecker.Parse(Release(tag), Current));

    [Fact]
    public void Prereleases_are_ignored() =>
        Assert.Null(UpdateChecker.Parse(Release("v9.0.0", prerelease: true), Current));

    [Theory]
    [InlineData("https://evil.example/releases/v0.3.0")]
    [InlineData("https://github.com/someone-else/ClaudeUsageTray/releases/tag/v0.3.0")]
    [InlineData("http://github.com/charitty-ccrp/ClaudeUsageTray/releases/tag/v0.3.0")]
    public void Only_this_repositorys_release_pages_are_ever_opened(string url) =>
        Assert.Null(UpdateChecker.Parse(Release("v0.3.0", url), Current));

    [Fact]
    public void Garbage_json_is_ignored() =>
        Assert.Null(UpdateChecker.Parse("{ nope", Current));

    [Fact]
    public async Task Network_failure_means_no_update_rather_than_an_error()
    {
        using var checker = new UpdateChecker(new FakeHandler(_ => throw new HttpRequestException("offline")));

        Assert.Null(await checker.CheckAsync(Current, CancellationToken.None));
    }

    [Fact]
    public async Task Calls_the_github_api_and_reads_the_reply()
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(Release("v1.0.0"))));
        using var checker = new UpdateChecker(handler);

        var update = await checker.CheckAsync(Current, CancellationToken.None);

        Assert.Equal(new Version(1, 0, 0), update?.Version);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("api.github.com", request.RequestUri!.Host);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task Non_success_status_means_no_update()
    {
        using var checker = new UpdateChecker(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));

        Assert.Null(await checker.CheckAsync(Current, CancellationToken.None));
    }
}
