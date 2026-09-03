using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Scheduling;
using QbitFlow.Infrastructure.Data;
using QbitFlow.Web.Realtime;

namespace QbitFlow.Web.Api;

internal static class RunsApi
{
    public static void MapRunsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/runs");

        api.MapPost("/{id:guid}/cancel", async (Guid id, AppDbContext db, SchedulerService scheduler, CancellationToken ct) =>
        {
            var run = await db.RunHistory.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null) return Results.NotFound();
            if (run.Status != RunStatus.Running) return Results.Conflict(new { reason = "not running" });

            var cancelled = scheduler.CancelRun(run.PipelineId);
            return Results.Json(new { cancelled });
        });

        // Server-Sent Events: replays the run's buffered log, then streams new lines live.
        api.MapGet("/{id:guid}/stream", async (Guid id, HttpContext http, RunLogBus bus, AppDbContext db, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            // seed from the DB in case the run finished before this connection
            var persisted = await db.RunLogEntries.AsNoTracking()
                .Where(e => e.RunId == id).OrderBy(e => e.Seq).ToListAsync(ct);
            var seenMax = 0L;
            foreach (var e in persisted)
            {
                await WriteEvent(http, "log", e, ct);
                seenMax = Math.Max(seenMax, e.Seq);
            }

            try
            {
                await foreach (var e in bus.StreamAsync(id, ct))
                {
                    if (e.Seq <= seenMax) continue;
                    await WriteEvent(http, "log", e, ct);
                }
            }
            catch (OperationCanceledException) { }

            await http.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
        });

        api.MapGet("/", async (Guid? pipelineId, int? limit, AppDbContext db) =>
        {
            var q = db.RunHistory.AsNoTracking();
            if (pipelineId is { } pid) q = q.Where(r => r.PipelineId == pid);

            var rows = await q
                .OrderByDescending(r => r.StartedUtc)
                .Take(Math.Clamp(limit ?? 50, 1, 500))
                .ToListAsync();

            return Results.Json(rows.Select(r => new
            {
                r.Id, r.PipelineId, trigger = r.Trigger.ToString(), status = r.Status.ToString(),
                r.DryRun, r.StartedUtc, r.FinishedUtc, r.DurationMs,
                r.TorrentsEvaluated, r.RulesEvaluated, r.ActionsApplied, r.ActionsWouldApply, r.ErrorCount,
            }));
        });

        api.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var run = await db.RunHistory.AsNoTracking()
                .Include(r => r.RuleResults)
                .FirstOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();

            return Results.Json(new
            {
                run.Id, run.PipelineId, trigger = run.Trigger.ToString(), status = run.Status.ToString(),
                run.DryRun, run.StartedUtc, run.FinishedUtc, run.DurationMs,
                run.TorrentsEvaluated, run.RulesEvaluated, run.ActionsApplied, run.ActionsWouldApply,
                run.ActionsSkipped, run.ErrorCount, run.SummaryJson,
                ruleResults = run.RuleResults.Select(rr => new
                {
                    rr.RuleId, rr.RuleName, rr.SuccessCount, rr.FailureCount, rr.ErrorCount,
                    rr.ActionsApplied, rr.ActionsWouldApply,
                }),
            });
        });

        api.MapGet("/{id:guid}/logs", async (Guid id, long? after, int? limit, AppDbContext db) =>
        {
            var afterSeq = after ?? 0;
            var rows = await db.RunLogEntries.AsNoTracking()
                .Where(e => e.RunId == id && e.Seq > afterSeq)
                .OrderBy(e => e.Seq)
                .Take(Math.Clamp(limit ?? 500, 1, 2000))
                .ToListAsync();

            return Results.Json(rows.Select(e => new
            {
                e.Seq, e.TimestampUtc, e.Level, e.Message, e.TorrentHash, e.RuleId,
            }));
        });
    }

    private static async Task WriteEvent(HttpContext http, string name, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await http.Response.WriteAsync($"event: {name}\ndata: {json}\n\n", Encoding.UTF8, ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
