using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Engine;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Web.Pages.Rules;

public class IndexModel(AppDbContext db, IRuleRunner ruleRunner) : PageModel
{
    public List<Rule> Rules { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Rules = await db.Rules.AsNoTracking().OrderBy(r => r.Priority).ThenBy(r => r.Name).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostToggleEnabledAsync(int id, CancellationToken ct)
    {
        var rule = await db.Rules.FindAsync([id], ct);
        if (rule is not null)
        {
            rule.Enabled = !rule.Enabled;
            rule.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var rule = await db.Rules.FindAsync([id], ct);
        if (rule is not null)
        {
            db.Rules.Remove(rule);
            await db.SaveChangesAsync(ct);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunNowAsync(int id, CancellationToken ct)
    {
        await ruleRunner.RunAsync(id, ct);
        return RedirectToPage();
    }
}
