using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;
using QbitFlow.Core.Matching;
using QbitFlow.Engine.Matching;
using QbitFlow.Infrastructure.Config;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Engine.Analytics;

public sealed record AnalyticsRunResult(
    int MediaSourcesOk, int MediaSourcesFailed,
    int MediaItems, int TorrentsScored, int TorrentsMatched,
    DateTimeOffset ComputedUtc);

public interface IAnalyticsService
{
    Task<AnalyticsRunResult> RefreshAsync(CancellationToken ct);
    Task<DateTimeOffset?> LastRunUtcAsync(CancellationToken ct);
}

/// <summary>
/// The ONLY thing that talks to Plex / Jellyfin. Pulls media + watch data across every enabled
/// media source, aggregates a recency-weighted watch total per media item, matches every torrent on
/// every qBittorrent instance to a media item, buckets by qBt category, and quantile-normalises each
/// bucket into a 0..1 watch-popularity score in <see cref="MediaScoreCache"/>. Rule passes read that cache.
/// </summary>
public sealed class AnalyticsService(
    IDbContextFactory<AppDbContext> dbFactory,
    ISourceAdapterFactory adapters,
    Sources.TorrentSnapshotCache snapshots,
    IMediaMatcher matcher,
    ILogger<AnalyticsService> log) : IAnalyticsService
{
    private const string LastRunKey = "AnalyticsLastRunUtc";

    public async Task<DateTimeOffset?> LastRunUtcAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var raw = (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == LastRunKey, ct))?.Value;
        return DateTimeOffset.TryParse(raw, out var v) ? v : null;
    }

    public async Task<AnalyticsRunResult> RefreshAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddYears(-2);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var weights = AnalyticsWeights.Parse(
            (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == AppSetting.AnalyticsWeights, ct))?.Value);

        var mediaSources = await db.SourceConnections.AsNoTracking()
            .Where(s => s.Enabled && (s.Kind == SourceKind.Plex || s.Kind == SourceKind.Jellyfin))
            .Select(s => s.Id).ToListAsync(ct);
        var qbtSources = await db.SourceConnections.AsNoTracking()
            .Where(s => s.Enabled && s.Kind == SourceKind.Qbt)
            .Select(s => s.Id).ToListAsync(ct);

        // ---- 1. fetch + upsert catalog ----
        var items = await db.MediaItems
            .Include(m => m.Files)
            .Include(m => m.SourceStats)
            .ToListAsync(ct);
        var byIdentity = items.ToDictionary(Identity, m => m);

        int okSources = 0, failedSources = 0;

        foreach (var sourceId in mediaSources)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<MediaRecord> media;
            IReadOnlyList<WatchRecord> watch;
            try
            {
                var adapter = adapters.GetMediaAdapter(sourceId);
                media = await adapter.FetchMediaAsync(ct);
                watch = await adapter.FetchWatchAsync(since, ct);
            }
            catch (Exception ex)
            {
                failedSources++;
                log.LogError(ex, "Analytics: media source {SourceId} failed — skipped", sourceId);
                continue;
            }
            okSources++;

            foreach (var rec in media)
                UpsertMediaItem(db, byIdentity, items, rec);

            foreach (var w in watch)
                UpsertWatch(db, byIdentity, sourceId, w, now);
        }

        // ---- 2. aggregate per media ----
        foreach (var item in items)
        {
            double total = 0;
            DateTimeOffset? last = null;
            foreach (var stat in item.SourceStats)
            {
                total += WeightedTotal(stat, weights);
                if (stat.LastPlayedUtc is { } lp && (last is null || lp > last)) last = lp;
            }
            item.WeightedWatchTotal = total;
            item.LastWatchedUtc = last;
            item.UpdatedUtc = now;
        }

        // legacy per-type quantile
        foreach (var group in items.GroupBy(i => NormalizeType(i.MediaType)))
        {
            var scored = Quantile.NormalizeQuantile(group.ToDictionary(i => i.Id.ToString(), i => i.WeightedWatchTotal));
            var byId = scored.ToDictionary(x => x.Key, x => x.Score);
            foreach (var i in group)
                i.LegacyScore = byId.GetValueOrDefault(i.Id.ToString());
        }

        await db.SaveChangesAsync(ct);

        // ---- 3. bucket torrents by qBt category, normalise ----
        var catalog = MediaCatalog.Build(items);
        var itemById = items.ToDictionary(i => i.Id);
        var scoreRows = new List<MediaScoreCache>();
        int torrentsScored = 0, torrentsMatched = 0;

        foreach (var qbtId in qbtSources)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<Core.Contracts.TorrentView> torrents;
            try
            {
                // Share the rule engine's snapshot: analytics runs far less often, so a snapshot a
                // few minutes old is fine and saves a duplicate pull.
                torrents = await snapshots.GetAsync(qbtId, TimeSpan.FromMinutes(5), ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Analytics: qBittorrent {SourceId} unreachable — no scores for it", qbtId);
                continue;
            }

            var buckets = new Dictionary<string, List<(string Hash, double Total, Guid? MediaId, double? DaysSince, bool Matched)>>();

            foreach (var t in torrents)
            {
                torrentsScored++;
                var match = matcher.Match(t, catalog);
                var category = string.IsNullOrWhiteSpace(t.Category) ? "(uncategorized)" : t.Category;

                double total = 0;
                double? daysSince = null;
                Guid? mediaId = null;
                var matched = false;

                if (match is not null && itemById.TryGetValue(match.MediaItemId, out var item))
                {
                    matched = true;
                    torrentsMatched++;
                    mediaId = item.Id;
                    total = item.WeightedWatchTotal;
                    if (item.LastWatchedUtc is { } lw) daysSince = (now - lw).TotalDays;
                }

                if (!buckets.TryGetValue(category, out var list)) buckets[category] = list = [];
                list.Add((t.Hash, total, mediaId, daysSince, matched));
            }

            foreach (var (category, rows) in buckets)
            {
                var normalized = Quantile.NormalizeQuantile(rows.ToDictionary(r => r.Hash, r => r.Total));
                var scoreByHash = normalized.ToDictionary(x => x.Key, x => x.Score);

                foreach (var r in rows)
                    scoreRows.Add(new MediaScoreCache
                    {
                        QbtInstanceId = qbtId,
                        TorrentHash = r.Hash,
                        MediaItemId = r.MediaId,
                        Category = category,
                        WatchTotal = r.Total,
                        WatchPopularity = scoreByHash.GetValueOrDefault(r.Hash),
                        DaysSinceLastWatched = r.DaysSince,
                        IsMediaMatched = r.Matched,
                        ComputedUtc = now,
                    });
            }

            // replace this instance's rows
            await db.MediaScoreCache.Where(s => s.QbtInstanceId == qbtId).ExecuteDeleteAsync(ct);
        }

        db.MediaScoreCache.AddRange(scoreRows);
        await UpsertSettingAsync(db, LastRunKey, now.ToString("o"), ct);
        await db.SaveChangesAsync(ct);

        log.LogInformation(
            "Analytics refresh: {Ok} source(s) ok, {Failed} failed, {Items} media items, {Scored} torrents scored ({Matched} matched)",
            okSources, failedSources, items.Count, torrentsScored, torrentsMatched);

        return new AnalyticsRunResult(okSources, failedSources, items.Count, torrentsScored, torrentsMatched, now);
    }

    // ---- helpers ----

    private static (string, string, int?) Identity(MediaItem m) =>
        (NormalizeType(m.MediaType), MediaKey.NormalizeTitle(m.Title), m.Year);

    private static string NormalizeType(string t) => t.ToLowerInvariant() switch
    {
        "episode" or "show" or "series" => "show",
        _ => "movie",
    };

    private void UpsertMediaItem(
        AppDbContext db,
        Dictionary<(string, string, int?), MediaItem> byIdentity,
        List<MediaItem> items,
        MediaRecord rec)
    {
        var id = (NormalizeType(rec.MediaType), MediaKey.NormalizeTitle(rec.Title), rec.Year);
        if (!byIdentity.TryGetValue(id, out var item))
        {
            item = new MediaItem
            {
                MatchKey = MediaKey.NormalizeTitle(rec.Title),
                Title = rec.Title,
                MediaType = NormalizeType(rec.MediaType),
                Year = rec.Year,
            };
            db.MediaItems.Add(item);
            byIdentity[id] = item;
            items.Add(item);
        }

        item.Rating ??= rec.Rating;
        if (rec.Genres.Count > 0) item.Genres = string.Join(",", rec.Genres);
        item.DurationMs ??= rec.DurationMs;

        var known = item.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in rec.Files)
        {
            if (string.IsNullOrWhiteSpace(f.Path) || !known.Add(f.Path)) continue;
            item.Files.Add(new MediaFilePath { Path = f.Path, FileName = f.FileName, SizeBytes = f.SizeBytes });
        }
    }

    private void UpsertWatch(
        AppDbContext db,
        Dictionary<(string, string, int?), MediaItem> byIdentity,
        Guid sourceId,
        WatchRecord w,
        DateTimeOffset now)
    {
        var type = NormalizeType(w.MediaType);
        var titleNorm = MediaKey.NormalizeTitle(w.Title);

        var target = byIdentity
            .Where(kv => kv.Key.Item1 == type && kv.Key.Item2 == titleNorm)
            .Select(kv => kv.Value)
            .OrderByDescending(m => m.Files.Count)
            .FirstOrDefault();

        if (target is null) return;

        var stat = target.SourceStats.FirstOrDefault(s => s.SourceConnectionId == sourceId);
        if (stat is null)
        {
            stat = new MediaSourceStat { MediaItemId = target.Id, SourceConnectionId = sourceId };
            target.SourceStats.Add(stat);
        }

        stat.PlayCount = w.PlayCount;
        stat.LastPlayedUtc = w.LastPlayedUtc;
        stat.WindowCountsJson = w.WindowCounts is { Count: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(w.WindowCounts)
            : null;
        stat.FetchedUtc = now;
    }

    private static double WeightedTotal(MediaSourceStat stat, AnalyticsWeights weights)
    {
        if (stat.WindowCountsJson is { Length: > 0 })
        {
            try
            {
                var windows = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(stat.WindowCountsJson);
                if (windows is { Count: > 0 })
                    return windows.Sum(kv => weights.For(kv.Key) * kv.Value);
            }
            catch { /* fall through */ }
        }
        return weights.All * stat.PlayCount;
    }

    private static async Task UpsertSettingAsync(AppDbContext db, string key, string value, CancellationToken ct)
    {
        var existing = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else existing.Value = value;
    }
}
