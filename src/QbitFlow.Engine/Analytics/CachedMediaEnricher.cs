using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Engine.Analytics;

/// <summary>
/// Serves the media / derived (<c>hotcold</c>, <c>watch_total</c>, …) fields for a torrent straight
/// from <see cref="Core.Domain.MediaScoreCache"/> + <see cref="Core.Domain.MediaItem"/> — the rows
/// the analytics job last wrote. A pipeline run never fetches from Plex/Jellyfin itself.
/// </summary>
public sealed class CachedMediaEnricher(IDbContextFactory<AppDbContext> dbFactory) : IMediaEnricher
{
    public async Task<IReadOnlyDictionary<string, object?>> EnrichAsync(
        Guid qbtInstanceId, TorrentView torrent, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var score = await db.MediaScoreCache.AsNoTracking()
            .FirstOrDefaultAsync(s => s.QbtInstanceId == qbtInstanceId && s.TorrentHash == torrent.Hash, ct);

        var media = score?.MediaItemId is { } mid
            ? await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid, ct)
            : null;

        var hotcold = score?.HotColdScore ?? 0d;
        var watchTotal = score?.WatchTotal ?? 0d;
        var daysSince = score?.DaysSinceLastWatched ?? 99999d;
        var matched = score?.IsMediaMatched ?? false;

        return new Dictionary<string, object?>
        {
            ["media_title"] = media?.Title ?? "",
            ["media_year"] = media?.Year ?? 0,
            ["media_rating"] = media?.Rating ?? 0d,
            ["media_genres"] = media?.Genres ?? "",
            ["media_type"] = media?.MediaType ?? "",
            ["media_duration_ms"] = media?.DurationMs ?? 0L,

            ["plex_title"] = media?.Title ?? "",
            ["plex_year"] = media?.Year ?? 0,
            ["plex_rating"] = media?.Rating ?? 0d,
            ["plex_viewCount"] = media is null ? 0 : (int)Math.Round(media.WeightedWatchTotal),
            ["plex_nview"] = hotcold,                       // alias
            ["plex_nview_legacy"] = media?.LegacyScore ?? 0d,

            ["hotcold"] = hotcold,
            ["watch_total"] = watchTotal,
            ["days_since_last_watched"] = daysSince,
            ["is_media_matched"] = matched,
        };
    }
}
