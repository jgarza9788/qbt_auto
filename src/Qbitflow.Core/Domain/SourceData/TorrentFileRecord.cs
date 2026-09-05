namespace Qbitflow.Core.Domain.SourceData;

/// <summary>
/// One file within a torrent, fetched on demand via IQbtTorrentFilesProvider (not part of
/// the bulk per-cycle fetch -- qBittorrent has no bulk "all torrents' files" endpoint, so
/// pulling this for every torrent on every refresh would mean one HTTP call per torrent).
/// </summary>
public class TorrentFileRecord
{
    public required string TorrentHash { get; init; }
    public required string FilePath { get; init; }
    public long SizeBytes { get; init; }
    public double Progress { get; init; }
}
