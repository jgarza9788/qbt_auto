using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Web.Pages.Instances;

public class EditStoragePathModel(AppDbContext db) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsNew => Input.Id is null;

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken ct)
    {
        if (id is not null)
        {
            var path = await db.StoragePaths.FindAsync([id.Value], ct);
            if (path is null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Id = path.Id,
                Name = path.Name,
                Path = path.Path,
                Enabled = path.Enabled,
                FolderSizeScanIntervalMinutes = path.FolderSizeScanIntervalMinutes
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var now = DateTimeOffset.UtcNow;
        StoragePathConfig path;

        if (Input.Id is { } id)
        {
            var existing = await db.StoragePaths.SingleOrDefaultAsync(s => s.Id == id, ct);
            if (existing is null)
            {
                return NotFound();
            }
            path = existing;
        }
        else
        {
            path = new StoragePathConfig { Name = Input.Name, Path = Input.Path, CreatedAt = now };
            db.StoragePaths.Add(path);
        }

        path.Name = Input.Name;
        path.Path = Input.Path;
        path.Enabled = Input.Enabled;
        path.FolderSizeScanIntervalMinutes = Input.FolderSizeScanIntervalMinutes;
        path.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            ModelState.AddModelError("Input.Name", "A storage path with this name already exists.");
            return Page();
        }

        return RedirectToPage("/Instances/Index");
    }

    public class InputModel
    {
        public int? Id { get; set; }

        [Required]
        public string Name { get; set; } = "";

        [Required]
        [Display(Name = "Path")]
        public string Path { get; set; } = "";

        public bool Enabled { get; set; } = true;

        [Range(5, 10_080)]
        [Display(Name = "Folder size scan interval (minutes)")]
        public int FolderSizeScanIntervalMinutes { get; set; } = 60;
    }
}
