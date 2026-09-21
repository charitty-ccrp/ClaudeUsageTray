using ClaudeUsageTray.Models;

namespace ClaudeUsageTray.Services;

/// <summary>Fake provider for UI development (run the app with --mock). Cycles through every colour band.</summary>
public sealed class MockUsageProvider : IUsageProvider
{
    private static readonly double[] Sequence = { 63, 72, 88, 96, 25 };

    private readonly DateTimeOffset _resetsAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(17);
    private int _calls;

    public Task<UsageFetchResult> GetUsageAsync(CancellationToken cancellationToken)
    {
        var value = Sequence[(Interlocked.Increment(ref _calls) - 1) % Sequence.Length];
        var weeklyReset = DateTimeOffset.UtcNow.Date.AddDays(3).AddHours(17);
        var usage = new UsageResponse(
            new UsageEntry(value, _resetsAt),
            new UsageEntry(18, weeklyReset),
            new UsageEntry(5, weeklyReset),
            null);
        return Task.FromResult(UsageFetchResult.Ok(usage));
    }

    public Task<string?> GetPlanNameAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("Pro");
}
