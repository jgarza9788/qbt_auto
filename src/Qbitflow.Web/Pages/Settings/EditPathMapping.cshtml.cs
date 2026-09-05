using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Web.Pages.Settings;

public class EditPathMappingModel(AppDbContext db) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsNew => Input.Id is null;

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken ct)
    {
        if (id is not null)
        {
            var rule = await db.PathMappingRules.FindAsync([id.Value], ct);
            if (rule is null)
            {
                return NotFound();
            }

            Input = new InputModel { Id = rule.Id, SourcePrefix = rule.SourcePrefix, CanonicalPrefix = rule.CanonicalPrefix, Enabled = rule.Enabled };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        PathMappingRule rule;
        if (Input.Id is { } id)
        {
            var existing = await db.PathMappingRules.SingleOrDefaultAsync(r => r.Id == id, ct);
            if (existing is null)
            {
                return NotFound();
            }
            rule = existing;
        }
        else
        {
            rule = new PathMappingRule { SourcePrefix = Input.SourcePrefix, CanonicalPrefix = Input.CanonicalPrefix, CreatedAt = DateTimeOffset.UtcNow };
            db.PathMappingRules.Add(rule);
        }

        rule.SourcePrefix = Input.SourcePrefix;
        rule.CanonicalPrefix = Input.CanonicalPrefix;
        rule.Enabled = Input.Enabled;

        await db.SaveChangesAsync(ct);
        return RedirectToPage("/Settings/Index");
    }

    public class InputModel
    {
        public int? Id { get; set; }

        [Required]
        [Display(Name = "Source prefix")]
        public string SourcePrefix { get; set; } = "";

        [Required]
        [Display(Name = "Canonical prefix")]
        public string CanonicalPrefix { get; set; } = "";

        public bool Enabled { get; set; } = true;
    }
}
