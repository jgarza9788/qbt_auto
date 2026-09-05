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
}
