using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Scheduling;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Api;

internal static class PipelinesApi
{
    public sealed record PipelineSourceInput(Guid SourceConnectionId, string Roles);

    public sealed record PipelineInput(
        string Name, bool Enabled, bool DryRun,
        string ScheduleKind, int? IntervalSeconds, string? CronExpression, string? TimeZoneId,
        int? MaxParallelism, bool? StopOnFirstMatch,
        IReadOnlyList<PipelineSourceInput>? Sources);

    public static void MapPipelinesApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/pipelines");

        api.MapGet("/", async (AppDbContext db) =>
        {
            var rows = await db.Pipelines.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Id, p.Name, p.Enabled, p.DryRun, p.IsRunning,
                    p.ScheduleKind, p.CronExpression, p.IntervalSeconds,
                    p.LastRunUtc, p.NextRunUtc,
                    ruleCount = p.Rules.Count,
                })
                .ToListAsync();

            return Results.Json(rows.Select(p => new
            {
                p.Id, p.Name, p.Enabled, p.DryRun, p.IsRunning,
                schedule = p.ScheduleKind == ScheduleKind.Cron
                    ? p.CronExpression
                    : $"every {Math.Max(p.IntervalSeconds ?? 300, 300)}s",
                p.LastRunUtc, p.NextRunUtc, p.ruleCount,
            }));
        });

        api.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var p = await db.Pipelines.AsNoTracking()
                .Include(x => x.Sources)
                .Include(x => x.Rules).ThenInclude(r => r.Action)
                .FirstOrDefaultAsync(x => x.Id == id);
            return p is null ? Results.NotFound() : Results.Json(ToDetail(p));
        });

        api.MapPost("/", async (PipelineInput input, AppDbContext db) =>
        {
            var p = new Pipeline();
            Apply(p, input);
            SyncSources(p, input, db);
            db.Pipelines.Add(p);
            await db.SaveChangesAsync();
            p = await LoadDetail(db, p.Id);
            return Results.Created($"/api/pipelines/{p!.Id}", ToDetail(p));
        });

        api.MapPut("/{id:guid}", async (Guid id, PipelineInput input, AppDbContext db) =>
        {
            var p = await db.Pipelines.Include(x => x.Sources).FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            Apply(p, input);
            SyncSources(p, input, db);
            p.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Json(ToDetail((await LoadDetail(db, id))!));
        });

        api.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var p = await db.Pipelines.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            db.Pipelines.Remove(p);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        api.MapPost("/{id:guid}/enable", (Guid id, AppDbContext db) => SetEnabled(id, true, db));
        api.MapPost("/{id:guid}/disable", (Guid id, AppDbContext db) => SetEnabled(id, false, db));

        api.MapPost("/{id:guid}/run", async (Guid id, bool? dryRun, AppDbContext db, SchedulerService scheduler, CancellationToken ct) =>
        {
            if (!await db.Pipelines.AnyAsync(p => p.Id == id, ct))
                return Results.NotFound();

            var started = await scheduler.TriggerNowAsync(id, dryRun, ct);
            return started
                ? Results.Accepted($"/api/pipelines/{id}", new { pipelineId = id, started = true })
                : Results.Conflict(new { pipelineId = id, started = false, reason = "already running" });
        });
    }

    private static async Task<IResult> SetEnabled(Guid id, bool enabled, AppDbContext db)
    {
        var p = await db.Pipelines.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return Results.NotFound();
        p.Enabled = enabled;
        p.UpdatedUtc = DateTimeOffset.UtcNow;
        if (enabled && p.NextRunUtc is null) p.NextRunUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Results.Json(new { p.Id, p.Enabled });
    }

    private static Task<Pipeline?> LoadDetail(AppDbContext db, Guid id) =>
        db.Pipelines.Include(x => x.Sources)
            .Include(x => x.Rules).ThenInclude(r => r.Action)
            .FirstOrDefaultAsync(x => x.Id == id);

    private static void SyncSources(Pipeline p, PipelineInput input, AppDbContext db)
    {
        if (input.Sources is null) return;

        db.PipelineSources.RemoveRange(p.Sources);
        p.Sources.Clear();

        var order = 0;
        foreach (var s in input.Sources)
        {
            var roles = PipelineSourceRoles.None;
            foreach (var part in s.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Enum.TryParse<PipelineSourceRoles>(part, true, out var r))
                    roles |= r;
            if (roles == PipelineSourceRoles.None) roles = PipelineSourceRoles.Data;

            p.Sources.Add(new PipelineSource { SourceConnectionId = s.SourceConnectionId, Roles = roles, Order = order++ });
        }
    }

    private static void Apply(Pipeline p, PipelineInput input)
    {
        p.Name = input.Name.Trim();
        p.Enabled = input.Enabled;
        p.DryRun = input.DryRun;
        p.ScheduleKind = Enum.TryParse<ScheduleKind>(input.ScheduleKind, true, out var sk) ? sk : ScheduleKind.Interval;
        p.IntervalSeconds = input.IntervalSeconds;
        p.CronExpression = input.CronExpression;
        p.TimeZoneId = string.IsNullOrWhiteSpace(input.TimeZoneId) ? "UTC" : input.TimeZoneId!;
        p.MaxParallelism = Math.Clamp(input.MaxParallelism ?? 8, 1, 64);
        p.StopOnFirstMatch = input.StopOnFirstMatch ?? false;
    }

    private static object ToDetail(Pipeline p) => new
    {
        p.Id, p.Name, p.Enabled, p.DryRun, p.IsRunning,
        scheduleKind = p.ScheduleKind.ToString(),
        p.IntervalSeconds, effectiveIntervalSeconds = p.EffectiveIntervalSeconds,
        p.CronExpression, p.TimeZoneId, p.MaxParallelism, p.StopOnFirstMatch,
        p.LastRunUtc, p.NextRunUtc, p.LastRunId,
        sources = p.Sources.Select(s => new { s.SourceConnectionId, roles = s.Roles.ToString() }),
        rules = p.Rules.OrderBy(r => r.Order).Select(r => new
        {
            r.Id, r.Name, r.Order, r.Enabled, mode = r.ConditionMode.ToString(),
            expression = r.CompiledExpression, r.CompileValid, r.CompileError,
            action = r.Action is null ? null : new { r.Action.Type, r.Action.ParamsJson },
        }),
    };
}
