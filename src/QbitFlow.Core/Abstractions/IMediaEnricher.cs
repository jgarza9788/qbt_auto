using QbitFlow.Core.Contracts;

namespace QbitFlow.Core.Abstractions;

/// <summary>
/// Supplies the media / derived (<c>watch_popularity</c>, <c>watch_total</c>, …) fields for a
/// torrent, read from the analytics cache. <c>NullMediaEnricher</c> returns everything unmatched /
/// zero; <c>CachedMediaEnricher</c> is the real cache-backed implementation.
/// </summary>
public interface IMediaEnricher
{
    /// <summary>Fields to merge into the evaluation context for one torrent on one qBt instance.</summary>
    Task<IReadOnlyDictionary<string, object?>> EnrichAsync(
        Guid qbtInstanceId, TorrentView torrent, CancellationToken ct);
}

public sealed class NullMediaEnricher : IMediaEnricher
{
    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>
    {
        ["media_title"] = "",
        ["media_year"] = 0,
        ["media_rating"] = 0d,
        ["media_genres"] = "",
        ["media_type"] = "",
        ["media_duration_ms"] = 0L,
        ["plex_title"] = "",
        ["plex_year"] = 0,
        ["plex_rating"] = 0d,
        ["plex_viewCount"] = 0,
        ["plex_nview"] = 0d,
        ["watch_popularity"] = 0d,
        ["hotcold"] = 0d,
        ["watch_total"] = 0d,
        ["days_since_last_watched"] = 99999d,
        ["is_media_matched"] = false,
    };

    public Task<IReadOnlyDictionary<string, object?>> EnrichAsync(
        Guid qbtInstanceId, TorrentView torrent, CancellationToken ct) => Task.FromResult(Empty);
}
