namespace Qbitflow.Core.Domain.SourceData;

/// <summary>One media library item (movie or episode) as reported by Plex or Jellyfin.</summary>
public class MediaItemRecord
{
    public required int InstanceId { get; init; }
    public required string InstanceName { get; init; }
    public required SourceType SourceType { get; init; }
    public required string ExternalKey { get; init; }
    public required string Title { get; init; }
    public string? MediaType { get; init; }
    public List<string> FilePaths { get; init; } = [];
    public DateTimeOffset? AddedAt { get; init; }
}
