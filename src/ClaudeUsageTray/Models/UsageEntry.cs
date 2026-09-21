namespace ClaudeUsageTray.Models;

/// <summary>One usage window as reported by Claude. Utilization is a percentage (0-100).</summary>
public sealed record UsageEntry(double Utilization, DateTimeOffset? ResetsAt);
