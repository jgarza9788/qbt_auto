using Microsoft.EntityFrameworkCore;
using QbitFlow.Engine.Analytics;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Api;

internal static class AnalyticsApi
{
    public static void MapAnalyticsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/analytics");

        api.MapGet("/status", async (AppDbContext db, IAnalyticsService svc, CancellationToken ct) =>
        {
            var last = await svc.LastRunUtcAsync(ct);
            var scoreCount = await db.MediaScoreCache.CountAsync(ct);
            var matched = await db.MediaScoreCache.CountAsync(s => s.IsMediaMatched, ct);
            var mediaItems = await db.MediaItems.CountAsync(ct);

            return Results.Json(new
            {
                lastRunUtc = last,
                mediaItems,
                scoredTorrents = scoreCount,
                matchedTorrents = matched,
                categories = await db.MediaScoreCache.Select(s => s.Category).Distinct().CountAsync(ct),
            });
        });

        api.MapGet("/scores", async (Guid? qbtInstanceId, string? category, int? limit, AppDbContext db) =>
        {
            var q = db.MediaScoreCache.AsNoTracking().AsQueryable();
            if (qbtInstanceId is { } id) q = q.Where(s => s.QbtInstanceId == id);
            if (!string.IsNullOrWhiteSpace(category)) q = q.Where(s => s.Category == category);

            var rows = await q
                .OrderBy(s => s.Category).ThenByDescending(s => s.WatchPopularity)
                .Take(Math.Clamp(limit ?? 200, 1, 2000))
                .ToListAsync();

            return Results.Json(rows.Select(s => new
            {
                s.QbtInstanceId, s.TorrentHash, s.Category,
                s.WatchTotal, s.WatchPopularity, s.DaysSinceLastWatched, s.IsMediaMatched, s.MediaItemId,
            }));
        });

        api.MapGet("/unmatched", async (Guid? qbtInstanceId, int? limit, AppDbContext db) =>
        {
            var q = db.MediaScoreCache.AsNoTracking().Where(s => !s.IsMediaMatched);
            if (qbtInstanceId is { } id) q = q.Where(s => s.QbtInstanceId == id);

            var rows = await q.OrderBy(s => s.Category).Take(Math.Clamp(limit ?? 200, 1, 2000)).ToListAsync();
            return Results.Json(rows.Select(s => new { s.QbtInstanceId, s.TorrentHash, s.Category }));
        });

        api.MapPost("/refresh", async (AnalyticsRefreshService refresher, CancellationToken ct) =>
        {
            var started = await refresher.TriggerAsync(ct);
            return started
                ? Results.Accepted("/api/analytics/status", new { started = true })
                : Results.Conflict(new { started = false, reason = "already running" });
        });
    }
}
