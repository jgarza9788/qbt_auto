using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Pages;

public class PipelineEditModel(AppDbContext db) : PageModel
{
    public Pipeline Pipeline { get; private set; } = new();
    public List<SourceConnection> AllSources { get; private set; } = [];

    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public bool Enabled { get; set; }
    [BindProperty] public bool DryRun { get; set; }
    [BindProperty] public string ScheduleKind { get; set; } = "Interval";
    [BindProperty] public int? IntervalSeconds { get; set; }
    [BindProperty] public string? CronExpression { get; set; }
    [BindProperty] public string TimeZoneId { get; set; } = "UTC";
    [BindProperty] public int MaxParallelism { get; set; } = 8;
    [BindProperty] public bool StopOnFirstMatch { get; set; }
    [BindProperty] public List<Guid> DataSourceIds { get; set; } = [];
    [BindProperty] public List<Guid> TargetIds { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var p = await db.Pipelines.Include(x => x.Sources).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        Pipeline = p;
        AllSources = await db.SourceConnections.AsNoTracking().OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync();

        Name = p.Name; Enabled = p.Enabled; DryRun = p.DryRun;
        ScheduleKind = p.ScheduleKind.ToString(); IntervalSeconds = p.IntervalSeconds;
        CronExpression = p.CronExpression; TimeZoneId = p.TimeZoneId;
        MaxParallelism = p.MaxParallelism; StopOnFirstMatch = p.StopOnFirstMatch;
        DataSourceIds = p.Sources.Where(s => s.Roles.HasFlag(PipelineSourceRoles.Data)).Select(s => s.SourceConnectionId).ToList();
        TargetIds = p.Sources.Where(s => s.Roles.HasFlag(PipelineSourceRoles.ActionTarget)).Select(s => s.SourceConnectionId).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var p = await db.Pipelines.Include(x => x.Sources).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();

        p.Name = Name.Trim();
        p.Enabled = Enabled;
        p.DryRun = DryRun;
        p.ScheduleKind = Enum.TryParse<ScheduleKind>(ScheduleKind, true, out var sk) ? sk : QbitFlow.Core.Domain.ScheduleKind.Interval;
        p.IntervalSeconds = IntervalSeconds;
        p.CronExpression = CronExpression;
        p.TimeZoneId = string.IsNullOrWhiteSpace(TimeZoneId) ? "UTC" : TimeZoneId.Trim();
        p.MaxParallelism = Math.Clamp(MaxParallelism, 1, 64);
        p.StopOnFirstMatch = StopOnFirstMatch;
        if (Enabled && p.NextRunUtc is null) p.NextRunUtc = DateTimeOffset.UtcNow;

        db.PipelineSources.RemoveRange(p.Sources);
        p.Sources.Clear();
        var order = 0;
        var all = DataSourceIds.Concat(TargetIds).Distinct();
        foreach (var sid in all)
        {
            var roles = PipelineSourceRoles.None;
            if (DataSourceIds.Contains(sid)) roles |= PipelineSourceRoles.Data;
            if (TargetIds.Contains(sid)) roles |= PipelineSourceRoles.ActionTarget;
            p.Sources.Add(new PipelineSource { SourceConnectionId = sid, Roles = roles, Order = order++ });
        }

        p.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        TempData["Msg"] = "Pipeline saved.";
        return RedirectToPage(new { id });
    }
}
