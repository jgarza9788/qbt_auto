namespace Qbitflow.Core.Domain.SourceData;

/// <summary>Disk usage (and, if scanned, recursive folder size) for one configured StoragePathConfig.</summary>
public class StorageUsageRecord
{
    public required int StoragePathId { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }

    /// <summary>False if the path doesn't exist / isn't mounted / couldn't be read -- callers must not crash on this.</summary>
    public bool Available { get; init; }
    public string? Error { get; init; }

    public long TotalBytes { get; init; }
    public long UsedBytes { get; init; }
    public long FreeBytes { get; init; }
    public double UsedPercent { get; init; }
    public double FreePercent { get; init; }

    public long? FolderSizeBytes { get; init; }
    public DateTimeOffset? FolderSizeComputedAt { get; init; }
}
