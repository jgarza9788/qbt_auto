namespace Qbitflow.Core.Domain.SourceData;

public class InstanceRefreshResult
{
    public required int InstanceId { get; init; }
    public required string InstanceName { get; init; }
    public required bool Success { get; init; }
    public bool FromCache { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>
/// Result of one refresh cycle across many instances. One instance failing never
/// prevents the others from being reported here -- see SourceRefreshCoordinator.
/// </summary>
public class SourceRefreshSummary
{
    public List<InstanceRefreshResult> Results { get; init; } = [];
}
