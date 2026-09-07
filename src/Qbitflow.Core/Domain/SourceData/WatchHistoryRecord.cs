namespace Qbitflow.Core.Domain.SourceData;

/// <summary>One playback/watch event as reported by Tautulli, Jellystat, Jellyglance, or Streamystats.</summary>
public class WatchHistoryRecord
{
    public required int InstanceId { get; init; }
    public required string InstanceName { get; init; }

    /// <summary>Which kind of source produced this event -- decides which snapshot table it lands in.</summary>
    public required SourceType SourceType { get; init; }

    public string? MediaTitle { get; init; }
    public string? FilePath { get; init; }
    public string? UserName { get; init; }
    public DateTimeOffset? WatchedAt { get; init; }
    public double? PercentComplete { get; init; }
}
