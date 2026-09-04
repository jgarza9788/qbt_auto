using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Pages;

public class RunsModel(AppDbContext db) : PageModel
{
    public record Row(Guid Id, string Trigger, string Status, bool DryRun,
        DateTimeOffset StartedUtc, long? DurationMs, int Torrents, int Applied, int WouldApply, int Errors);

    public List<Row> Runs { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Runs = await db.RunHistory.AsNoTracking()
            .OrderByDescending(r => r.StartedUtc).Take(100)
            .Select(r => new Row(r.Id, r.Trigger.ToString(), r.Status.ToString(), r.DryRun,
                r.StartedUtc, r.DurationMs, r.TorrentsEvaluated, r.ActionsApplied, r.ActionsWouldApply, r.ErrorCount))
            .ToListAsync();
    }
}
