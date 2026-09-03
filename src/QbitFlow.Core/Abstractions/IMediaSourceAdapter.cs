using QbitFlow.Core.Domain;

namespace QbitFlow.Core.Abstractions;

public sealed record MediaFile(string Path, string FileName, long? SizeBytes);

public sealed record MediaRecord(
    string SourceItemId,
    string Title,
    string MediaType,           // movie | show | episode
    int? Year,
    double? Rating,
    IReadOnlyList<string> Genres,
    long? DurationMs,
    IReadOnlyList<MediaFile> Files);

public sealed record WatchRecord(
    string SourceItemId,
    string Title,
    string MediaType,
    int PlayCount,
    DateTimeOffset? LastPlayedUtc,
    /// <summary>Optional per-window counts (keys: all/year/month/week) when the source exposes them.</summary>
    IReadOnlyDictionary<string, int>? WindowCounts = null);

/// <summary>
/// A media library source (Plex or Jellyfin). The analytics job (Phase 3) calls
/// <see cref="FetchMediaAsync"/> + <see cref="FetchWatchAsync"/>; <see cref="SourceHealth"/> callers
/// use <see cref="TestAsync"/>.
/// </summary>
public interface IMediaSourceAdapter
{
    SourceKind Kind { get; }
    Guid SourceId { get; }

    Task<HealthResult> TestAsync(CancellationToken ct);
    Task<IReadOnlyList<MediaRecord>> FetchMediaAsync(CancellationToken ct);
    Task<IReadOnlyList<WatchRecord>> FetchWatchAsync(DateTimeOffset since, CancellationToken ct);
}

/// <summary>Resolves adapters (media + qBittorrent) from a <c>SourceConnection</c> id.</summary>
public interface ISourceAdapterFactory
{
    IMediaSourceAdapter GetMediaAdapter(Guid sourceConnectionId);
    IQbtAdapter GetQbtAdapter(Guid sourceConnectionId);
    IQbtActionTarget GetQbtActionTarget(Guid sourceConnectionId);

    /// <summary>Drop any cached client for this connection (call after an edit).</summary>
    void Invalidate(Guid sourceConnectionId);
}
