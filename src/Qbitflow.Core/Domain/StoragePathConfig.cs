namespace Qbitflow.Core.Domain;

/// <summary>
/// A user-defined named path (volume, drive, or folder) to monitor for disk usage
/// and, optionally, cached recursive folder size.
/// </summary>
public class StoragePathConfig
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>How often (minutes) to recompute recursive folder size. Folder size scans are expensive.</summary>
    public int FolderSizeScanIntervalMinutes { get; set; } = 60;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
