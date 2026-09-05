namespace Qbitflow.Core.Domain.SourceData;

/// <summary>
/// Everything one adapter fetched for one instance in a refresh cycle. Only the lists
/// relevant to that source's type are populated -- e.g. a qBittorrent fetch only fills
/// Torrents, leaving MediaItems/WatchHistory empty.
/// </summary>
public class SourceFetchResult
{
    public List<TorrentRecord> Torrents { get; init; } = [];
    public List<MediaItemRecord> MediaItems { get; init; } = [];
    public List<WatchHistoryRecord> WatchHistory { get; init; } = [];
}
