namespace Qbitflow.Core.Domain;

/// <summary>
/// Rewrites one instance's view of a path onto a canonical prefix so a qBittorrent
/// container mount (e.g. /downloads) and a Plex/Jellyfin container mount for the same
/// underlying files (e.g. /media/downloads) normalize to the same path_key and can be
/// joined in the snapshot. The first enabled rule whose SourcePrefix matches wins.
/// </summary>
public class PathMappingRule
{
    public int Id { get; set; }
    public required string SourcePrefix { get; set; }
    public required string CanonicalPrefix { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}
