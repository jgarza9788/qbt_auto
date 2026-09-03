namespace QbitFlow.Core.Contracts;

/// <summary>
/// A provider-neutral snapshot of one torrent. Mapped from the qBittorrent client in the Sources
/// project so Core never depends on a specific client library.
/// </summary>
public sealed record TorrentView
{
    public required string Hash { get; init; }
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string SavePath { get; init; } = "";
    public string ContentPath { get; init; } = "";
    public string State { get; init; } = "";

    public long Size { get; init; }
    public long Downloaded { get; init; }
    public long Uploaded { get; init; }
    public double Progress { get; init; }
    public double Ratio { get; init; }

    public long DownloadLimit { get; init; }
    public long UploadLimit { get; init; }

    public int NumSeeds { get; init; }
    public int NumLeechs { get; init; }

    /// <summary>How long the torrent has been active (qBittorrent's <c>active_time</c>).</summary>
    public TimeSpan ActiveTime { get; init; }

    public long ActiveTimeSeconds => (long)ActiveTime.TotalSeconds;

    public DateTimeOffset? AddedOn { get; init; }
    public DateTimeOffset? CompletionOn { get; init; }
    public DateTimeOffset? LastActivityTime { get; init; }
    public DateTimeOffset? LastSeenComplete { get; init; }

    public bool ForceStart { get; init; }
    public bool AutoManaged { get; init; }

    /// <summary>Convenience: <c>Tags</c> joined with commas, matching the legacy criteria surface.</summary>
    public string TagsCsv => string.Join(",", Tags);
}
