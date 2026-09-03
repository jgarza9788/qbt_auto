using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Pages;

public class PipelinesModel(AppDbContext db) : PageModel
{
    public record Row(Guid Id, string Name, bool Enabled, bool DryRun, bool IsRunning, string Schedule, int Rules, int Targets);

    public List<Row> Pipelines { get; private set; } = [];

    [BindProperty] public string NewName { get; set; } = "";

    public async Task OnGetAsync()
    {
        var rows = await db.Pipelines.AsNoTracking()
            .Select(p => new
            {
                p.Id, p.Name, p.Enabled, p.DryRun, p.IsRunning, p.ScheduleKind, p.CronExpression, p.IntervalSeconds,
                Rules = p.Rules.Count,
                Targets = p.Sources.Count(s => s.Roles.HasFlag(PipelineSourceRoles.ActionTarget)),
            })
            .ToListAsync();

        Pipelines = rows.OrderBy(p => p.Name).Select(p => new Row(
            p.Id, p.Name, p.Enabled, p.DryRun, p.IsRunning,
            p.ScheduleKind == ScheduleKind.Cron ? (p.CronExpression ?? "cron") : $"every {Math.Max(p.IntervalSeconds ?? 300, 300)}s",
            p.Rules, p.Targets)).ToList();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!string.IsNullOrWhiteSpace(NewName))
        {
            var p = new Pipeline { Name = NewName.Trim(), Enabled = false, DryRun = true, IntervalSeconds = 900 };
            db.Pipelines.Add(p);
            await db.SaveChangesAsync();
            return RedirectToPage("PipelineEdit", new { id = p.Id });
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        if (await db.Pipelines.FirstOrDefaultAsync(x => x.Id == id) is { } p)
        {
            db.Pipelines.Remove(p);
            await db.SaveChangesAsync();
        }
        TempData["Msg"] = "Pipeline deleted.";
        return RedirectToPage();
    }
}
