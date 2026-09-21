using ClaudeUsageTray.Models;

namespace ClaudeUsageTray.Services;

/// <summary>Chooses the active data source per call so switching mode in Setup takes effect without a restart.</summary>
public sealed class RoutingUsageProvider(Func<AuthMode> mode, IUsageProvider webCookie, IUsageProvider claudeCodeOAuth) : IUsageProvider
{
    private IUsageProvider Active => mode() == AuthMode.ClaudeCodeOAuth ? claudeCodeOAuth : webCookie;

    public Task<UsageFetchResult> GetUsageAsync(CancellationToken cancellationToken) => Active.GetUsageAsync(cancellationToken);

    public Task<string?> GetPlanNameAsync(CancellationToken cancellationToken) => Active.GetPlanNameAsync(cancellationToken);
}
