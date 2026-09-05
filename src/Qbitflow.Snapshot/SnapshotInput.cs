using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Snapshot;

/// <summary>Everything one snapshot rebuild ingests: the current fetch results across all instances, plus config needed to normalize paths.</summary>
public class SnapshotInput
{
    public List<TorrentRecord> Torrents { get; init; } = [];
    public List<TorrentFileRecord> TorrentFiles { get; init; } = [];
    public List<MediaItemRecord> MediaItems { get; init; } = [];
    public List<WatchHistoryRecord> WatchHistory { get; init; } = [];
    public List<StorageUsageRecord> StoragePaths { get; init; } = [];
    public List<PathMappingRule> PathMappingRules { get; init; } = [];
}
