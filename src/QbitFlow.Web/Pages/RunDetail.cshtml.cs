using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Pages;

public class RunDetailModel(AppDbContext db) : PageModel
{
    public RunHistory Run { get; private set; } = new();
    public List<RunRuleResult> RuleResults { get; private set; } = [];
    public List<RunLogEntry> Log { get; private set; } = [];
    public bool IsRunning => Run.Status == RunStatus.Running;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var run = await db.RunHistory.AsNoTracking().Include(r => r.RuleResults).FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return NotFound();
        Run = run;
        RuleResults = run.RuleResults.OrderByDescending(x => x.SuccessCount).ToList();
        Log = await db.RunLogEntries.AsNoTracking().Where(e => e.RunId == id).OrderBy(e => e.Seq).ToListAsync();
        return Page();
    }
}
