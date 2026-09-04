namespace QbitFlow.Core.Domain;

public class MediaItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string MatchKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string MediaType { get; set; } = "";   // movie | show | episode
    public int? Year { get; set; }
    public double? Rating { get; set; }
    public string Genres { get; set; } = "";      // csv
    public long? DurationMs { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Recency-weighted watch total across every source, set by the analytics refresh.</summary>
    public double WeightedWatchTotal { get; set; }

    /// <summary>Quantile of <see cref="WeightedWatchTotal"/> within this item's media type (legacy plex_nview semantics).</summary>
    public double LegacyScore { get; set; }

    public DateTimeOffset? LastWatchedUtc { get; set; }

    public List<MediaFilePath> Files { get; set; } = [];
    public List<MediaSourceStat> SourceStats { get; set; } = [];
}

public class MediaFilePath
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public long? SizeBytes { get; set; }
}

public class MediaSourceStat
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    public Guid SourceConnectionId { get; set; }

    public int PlayCount { get; set; }
    public DateTimeOffset? LastPlayedUtc { get; set; }
    public double? SourceRating { get; set; }

    /// <summary>Per-window play counts (all/year/month/week) as JSON, when the source provides them.</summary>
    public string? WindowCountsJson { get; set; }

    public DateTimeOffset FetchedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The per-torrent watch-popularity result, produced by the analytics job and read by rule runs.</summary>
public class MediaScoreCache
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid QbtInstanceId { get; set; }
    public string TorrentHash { get; set; } = "";

    public Guid? MediaItemId { get; set; }
    public string Category { get; set; } = "";

    public double WatchTotal { get; set; }

    /// <summary>0..1 quantile of the matched media's watch total, within this torrent's qBt category.</summary>
    public double WatchPopularity { get; set; }
    public double? DaysSinceLastWatched { get; set; }
    public bool IsMediaMatched { get; set; }

    public DateTimeOffset ComputedUtc { get; set; } = DateTimeOffset.UtcNow;
}
