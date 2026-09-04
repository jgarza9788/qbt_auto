using QbitFlow.Core.Domain;
using QbitFlow.Engine.RuleEngine;
using QbitFlow.Infrastructure.Config;

namespace QbitFlow.Web.Api;

/// <summary>
/// Control surface for the single rule engine: read its state, trigger a pass, pause/resume it, and
/// cancel an in-flight pass. This replaced the per-pipeline endpoints — there is one engine now, so
/// none of these take an id.
/// </summary>
internal static class EngineApi
{
    public static void MapEngineApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/engine");

        // Current engine state — the same values the dashboard card renders.
        api.MapGet("/", async (RuleEngineService engine, AppSettingStore settings, CancellationToken ct) =>
        {
            var cfg = await settings.GetEngineSettingsAsync(ct);
            return Results.Json(new
            {
                enabled = cfg.Enabled,
                running = engine.IsRunning,
                intervalSeconds = cfg.IntervalSeconds,
                dryRun = cfg.DryRun,
                maxParallelism = cfg.MaxParallelism,
                stopOnFirstMatch = cfg.StopOnFirstMatch,
            });
        });

        // Run a pass now. `dryRun` overrides the stored default for this pass only.
        api.MapPost("/run", async (bool? dryRun, RuleEngineService engine, CancellationToken ct) =>
            await engine.TriggerAsync(dryRun, ct) switch
            {
                TriggerOutcome.Started => Results.Accepted("/api/engine", new { started = true }),
                TriggerOutcome.AlreadyRunning => Results.Conflict(new { started = false, reason = "already running" }),
                TriggerOutcome.Paused => Results.Conflict(new { started = false, reason = "engine paused" }),
                _ => Results.Conflict(new { started = false, reason = "unknown" }),
            });

        api.MapPost("/enable", (AppSettingStore settings, CancellationToken ct) => SetEnabled(settings, true, ct));
        api.MapPost("/disable", (AppSettingStore settings, CancellationToken ct) => SetEnabled(settings, false, ct));

        // Cancels the in-flight pass; the run is recorded as Cancelled. No-op when idle.
        api.MapPost("/cancel", (RuleEngineService engine) =>
            Results.Json(new { cancelled = engine.CancelRun() }));
    }

    /// <summary>
    /// Pausing only stops new passes from starting — it does not cancel one already running (use
    /// <c>/cancel</c> for that) and it leaves the rules themselves untouched.
    /// </summary>
    private static async Task<IResult> SetEnabled(AppSettingStore settings, bool enabled, CancellationToken ct)
    {
        await settings.SetAsync(AppSetting.EngineEnabled, enabled.ToString(), ct);
        return Results.Json(new { enabled });
    }
}
