using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Analytics;
using QbitFlow.Engine.Scheduling;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Pages;

public class IndexModel(AppDbContext db, SchedulerService scheduler, IAnalyticsService analytics) : PageModel
{
    public record PipelineCard(Guid Id, string Name, bool Enabled, bool DryRun, bool IsRunning,
        string Schedule, int RuleCount, DateTimeOffset? LastRunUtc, DateTimeOffset? NextRunUtc);
    public record RunRow(Guid Id, string Pipeline, string Status, bool DryRun, DateTimeOffset StartedUtc,
        long? DurationMs, int Applied, int WouldApply, int Errors);

    public List<PipelineCard> Pipelines { get; private set; } = [];
    public List<RunRow> Runs { get; private set; } = [];
    public (int Total, int Healthy, int Bad) Sources { get; private set; }
    public DateTimeOffset? AnalyticsLastRun { get; private set; }

    public async Task OnGetAsync()
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Select(p => new { p.Id, p.Name, p.Enabled, p.DryRun, p.IsRunning, p.ScheduleKind, p.CronExpression, p.IntervalSeconds, p.LastRunUtc, p.NextRunUtc, Rules = p.Rules.Count })
            .ToListAsync();

        Pipelines = pipelines.OrderBy(p => p.Name).Select(p => new PipelineCard(
            p.Id, p.Name, p.Enabled, p.DryRun, p.IsRunning,
            p.ScheduleKind == ScheduleKind.Cron ? (p.CronExpression ?? "cron") : $"every {Math.Max(p.IntervalSeconds ?? 300, 300)}s",
            p.Rules, p.LastRunUtc, p.NextRunUtc)).ToList();

        var names = pipelines.ToDictionary(p => p.Id, p => p.Name);
        var runs = await db.RunHistory.AsNoTracking().OrderByDescending(r => r.StartedUtc).Take(12).ToListAsync();
        Runs = runs.Select(r => new RunRow(r.Id, names.GetValueOrDefault(r.PipelineId, "—"),
            r.Status.ToString(), r.DryRun, r.StartedUtc, r.DurationMs, r.ActionsApplied, r.ActionsWouldApply, r.ErrorCount)).ToList();

        var srcs = await db.SourceConnections.AsNoTracking().Select(s => s.HealthState).ToListAsync();
        Sources = (srcs.Count, srcs.Count(h => h == HealthState.Healthy), srcs.Count(h => h is HealthState.Unreachable or HealthState.Degraded));

        AnalyticsLastRun = await analytics.LastRunUtcAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostRunAsync(Guid id)
    {
        await scheduler.TriggerNowAsync(id, dryRun: null, HttpContext.RequestAborted);
        TempData["Msg"] = "Run queued.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id)
    {
        var p = await db.Pipelines.FirstOrDefaultAsync(x => x.Id == id);
        if (p is not null)
        {
            p.Enabled = !p.Enabled;
            if (p.Enabled && p.NextRunUtc is null) p.NextRunUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        return RedirectToPage();
    }
}
