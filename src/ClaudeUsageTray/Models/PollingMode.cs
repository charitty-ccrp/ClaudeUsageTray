namespace ClaudeUsageTray.Models;

/// <summary>How eagerly to poll; see PollingSchedule.Intervals for the actual numbers.</summary>
public enum PollingMode
{
    Balanced,
    Frequent,
    Saver,
}
