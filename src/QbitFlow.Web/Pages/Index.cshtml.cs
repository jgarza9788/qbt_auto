using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Analytics;
using QbitFlow.Engine.RuleEngine;
using QbitFlow.Infrastructure.Config;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Pages;

/// <summary>
/// Dashboard: engine state at a glance plus recent runs. Read-only except for the two controls that
/// map onto <see cref="RuleEngineService"/> — run a pass now, and pause/resume the engine.
/// </summary>
public class IndexModel(
    AppDbContext db,
    RuleEngineService engine,
    AppSettingStore settings,
    IAnalyticsService analytics) : PageModel
{
    public record RunRow(Guid Id, string Status, bool DryRun, DateTimeOffset StartedUtc,
        long? DurationMs, int Applied, int WouldApply, int Errors);

    public EngineSettings Engine { get; private set; } = EngineSettings.Fallback;
    public bool EngineRunning { get; private set; }
    public int EnabledRules { get; private set; }
    public int TotalRules { get; private set; }
    public DateTimeOffset? LastRunUtc { get; private set; }

    /// <summary>
    /// Approximate: the loop sleeps one interval after each pass, so the next one lands about an
    /// interval after the last started. It is a hint for the user, not a scheduled time — there is no
    /// stored next-run because there is no schedule.
    /// </summary>
    public DateTimeOffset? NextRunUtc { get; private set; }

    public List<RunRow> Runs { get; private set; } = [];
    public (int Total, int Healthy, int Bad) Sources { get; private set; }
    public DateTimeOffset? AnalyticsLastRun { get; private set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;

        Engine = await settings.GetEngineSettingsAsync(ct);
        EngineRunning = engine.IsRunning;

        TotalRules = await db.Rules.CountAsync(ct);
        EnabledRules = await db.Rules.CountAsync(r => r.Enabled, ct);

        LastRunUtc = await db.RunHistory.AsNoTracking()
            .OrderByDescending(r => r.StartedUtc)
            .Select(r => (DateTimeOffset?)r.StartedUtc)
            .FirstOrDefaultAsync(ct);
        NextRunUtc = (LastRunUtc ?? DateTimeOffset.UtcNow) + Engine.Interval;

        Runs = await db.RunHistory.AsNoTracking().OrderByDescending(r => r.StartedUtc).Take(12)
            .Select(r => new RunRow(r.Id, r.Status.ToString(), r.DryRun, r.StartedUtc,
                r.DurationMs, r.ActionsApplied, r.ActionsWouldApply, r.ErrorCount))
            .ToListAsync(ct);

        var health = await db.SourceConnections.AsNoTracking().Select(s => s.HealthState).ToListAsync(ct);
        Sources = (health.Count,
            health.Count(h => h == HealthState.Healthy),
            health.Count(h => h is HealthState.Unreachable or HealthState.Degraded));

        AnalyticsLastRun = await analytics.LastRunUtcAsync(ct);
    }

    /// <summary>Runs a pass immediately. Reports back when the engine was paused or already busy.</summary>
    public async Task<IActionResult> OnPostRunAsync()
    {
        TempData["Msg"] = await engine.TriggerAsync(dryRun: null, HttpContext.RequestAborted) switch
        {
            TriggerOutcome.Started => "Rule pass triggered.",
            TriggerOutcome.AlreadyRunning => "A pass is already running.",
            TriggerOutcome.Paused => "Engine is paused — resume it first.",
            _ => "Could not start a pass.",
        };
        return RedirectToPage();
    }

    /// <summary>Pauses or resumes the loop. Does not touch an in-flight pass or the rules themselves.</summary>
    public async Task<IActionResult> OnPostToggleAsync()
    {
        var ct = HttpContext.RequestAborted;
        var enabled = (await settings.GetEngineSettingsAsync(ct)).Enabled;
        await settings.SetAsync(AppSetting.EngineEnabled, (!enabled).ToString(), ct);
        TempData["Msg"] = enabled ? "Engine paused." : "Engine resumed.";
        return RedirectToPage();
    }
}
