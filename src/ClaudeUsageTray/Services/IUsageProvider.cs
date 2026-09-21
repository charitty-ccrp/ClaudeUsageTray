using ClaudeUsageTray.Models;

namespace ClaudeUsageTray.Services;

/// <summary>Source of usage data. The UI and UsageService only depend on this, never on HttpClient.</summary>
public interface IUsageProvider
{
    Task<UsageFetchResult> GetUsageAsync(CancellationToken cancellationToken);

    /// <summary>Best-effort plan name (e.g. "Pro"). Null when unknown; must not throw for ordinary failures.</summary>
    Task<string?> GetPlanNameAsync(CancellationToken cancellationToken);
}
