using System.Text.RegularExpressions;
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

    public async Task<IActionResult> OnPostDuplicateAsync(int id, CancellationToken ct)
    {
        var rule = await db.Rules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
        {
            return RedirectToPage();
        }

        var now = DateTimeOffset.UtcNow;
        var copy = new Rule
        {
            Name = await NextCopyNameAsync(rule.Name, ct),
            Description = rule.Description,
            // A copy starts disabled so it cannot fire on the original's schedule before
            // the user has had a chance to adjust it.
            Enabled = false,
            Priority = rule.Priority,
            StopOnMatch = rule.StopOnMatch,
            DryRun = rule.DryRun,
            CronExpression = rule.CronExpression,
            TimeZoneId = rule.TimeZoneId,
            ConditionTreeJson = rule.ConditionTreeJson,
            AdvancedSqlWhere = rule.AdvancedSqlWhere,
            UseAdvancedSql = rule.UseAdvancedSql,
            ActionsJson = rule.ActionsJson,
            TargetInstanceIdsJson = rule.TargetInstanceIdsJson,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Rules.Add(copy);
        await db.SaveChangesAsync(ct);
        return RedirectToPage("/Rules/Edit", new { id = copy.Id });
    }

    /// <summary>"Foo" -> "Foo (copy)", then "Foo (copy 2)", "Foo (copy 3)", ... for further copies.</summary>
    private async Task<string> NextCopyNameAsync(string sourceName, CancellationToken ct)
    {
        var baseName = Regex.Replace(sourceName, @"\s*\(copy(?: \d+)?\)$", "");
        var taken = await db.Rules.AsNoTracking()
            .Where(r => r.Name.StartsWith(baseName))
            .Select(r => r.Name)
            .ToListAsync(ct);

        var candidate = $"{baseName} (copy)";
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = $"{baseName} (copy {n})";
        }

        return candidate;
    }

    public async Task<IActionResult> OnPostRunNowAsync(int id, CancellationToken ct)
    {
        await ruleRunner.RunAsync(id, ct);
        return RedirectToPage();
    }
}
