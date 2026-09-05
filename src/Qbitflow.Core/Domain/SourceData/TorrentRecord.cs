namespace Qbitflow.Core.Domain.SourceData;

/// <summary>One torrent as reported by a qBittorrent instance's /torrents/info.</summary>
public class TorrentRecord
{
    public required int InstanceId { get; init; }
    public required string InstanceName { get; init; }
    public required string Hash { get; init; }
    public required string Name { get; init; }
    public string? Category { get; init; }
    public List<string> Tags { get; init; } = [];
    public string? SavePath { get; init; }
    public string? ContentPath { get; init; }
    public long SizeBytes { get; init; }
    public double Progress { get; init; }
    public string? State { get; init; }
    public long DownloadedBytes { get; init; }
    public long UploadedBytes { get; init; }
    public double Ratio { get; init; }
    public DateTimeOffset? AddedOn { get; init; }
    public DateTimeOffset? CompletionOn { get; init; }
    public long UploadLimitBytesPerSec { get; init; }
    public long DownloadLimitBytesPerSec { get; init; }

    /// <summary>Currently-working tracker URL (empty when qBittorrent has no working tracker).</summary>
    public string? Tracker { get; init; }
    /// <summary>Full size of all selected files (>= <see cref="SizeBytes"/>, which counts wanted files only).</summary>
    public long TotalSizeBytes { get; init; }
    /// <summary>Bytes still to download (0 once complete).</summary>
    public long AmountLeftBytes { get; init; }
    /// <summary>Bytes of the selected content already downloaded.</summary>
    public long CompletedBytes { get; init; }
    /// <summary>Current download rate in bytes/sec.</summary>
    public long DownloadSpeedBytesPerSec { get; init; }
    /// <summary>Current upload rate in bytes/sec.</summary>
    public long UploadSpeedBytesPerSec { get; init; }
    /// <summary>Estimated seconds to completion. qBittorrent reports 8640000 (100 days) as "infinity".</summary>
    public long EtaSeconds { get; init; }
    /// <summary>Seconds spent seeding.</summary>
    public long SeedingTimeSeconds { get; init; }
    /// <summary>Seconds the torrent has been active (downloading or seeding).</summary>
    public long ActiveTimeSeconds { get; init; }
    /// <summary>Connected seeds (peers with the full torrent we're connected to).</summary>
    public long ConnectedSeeds { get; init; }
    /// <summary>Seeds in the swarm as reported by the tracker (num_complete).</summary>
    public long TotalSeeds { get; init; }
    /// <summary>Connected leechers.</summary>
    public long ConnectedLeechers { get; init; }
    /// <summary>Leechers in the swarm as reported by the tracker (num_incomplete).</summary>
    public long TotalLeechers { get; init; }
    /// <summary>Fraction of the torrent available across connected peers; qBittorrent reports -1 when unknown.</summary>
    public double Availability { get; init; }
    /// <summary>Whether Automatic Torrent Management is enabled for this torrent.</summary>
    public bool AutoTmmEnabled { get; init; }
    /// <summary>Per-torrent share-ratio limit: -2 = use global, -1 = unlimited, otherwise the ratio.</summary>
    public double RatioLimit { get; init; }
    /// <summary>Per-torrent seeding-time limit in minutes: -2 = use global, -1 = unlimited, otherwise minutes.</summary>
    public long SeedingTimeLimitMinutes { get; init; }
    /// <summary>When the torrent last had tracker/peer activity.</summary>
    public DateTimeOffset? LastActivityOn { get; init; }
    /// <summary>When a complete copy was last seen in the swarm.</summary>
    public DateTimeOffset? SeenCompleteOn { get; init; }
}
