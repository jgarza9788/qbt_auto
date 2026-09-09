namespace Qbitflow.Core.Domain.SourceData;

/// <summary>Current live state of one torrent, fetched fresh right before applying actions -- used for idempotency checks, not the (possibly cycle-stale) snapshot.</summary>
public class QbtTorrentState
{
    public required string Hash { get; init; }

    /// <summary>qBittorrent's torrent <c>name</c>. Used to name exported .torrent files.</summary>
    public string? Name { get; init; }

    public HashSet<string> Tags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Category { get; init; }
    public string? SavePath { get; init; }
    public long UploadLimitBytesPerSec { get; init; }
    public long DownloadLimitBytesPerSec { get; init; }

    /// <summary>qBittorrent's raw <c>state</c> string (e.g. "downloading", "stoppedUP", "pausedDL"). Used for start/stop idempotency.</summary>
    public string? State { get; init; }
}
