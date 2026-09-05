using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Infrastructure.Persistence;
using Qbitflow.Infrastructure.Security;

namespace Qbitflow.Web.Pages.Instances;

public class EditModel(AppDbContext db, ISecretProtector secretProtector) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsNew => Input.Id is null;

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken ct)
    {
        if (id is not null)
        {
            var instance = await db.Instances.FindAsync([id.Value], ct);
            if (instance is null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Id = instance.Id,
                Name = instance.Name,
                SourceType = instance.SourceType,
                BaseUrl = instance.BaseUrl,
                Username = instance.Username,
                Enabled = instance.Enabled,
                TimeoutSeconds = instance.TimeoutSeconds,
                VerifySsl = instance.VerifySsl,
                ExtraConfigJson = instance.ExtraConfigJson,
                HasExistingPassword = instance.PasswordProtected is not null,
                HasExistingApiKey = instance.ApiKeyProtected is not null
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Input.ExtraConfigJson))
        {
            try
            {
                JsonDocument.Parse(Input.ExtraConfigJson);
            }
            catch (JsonException)
            {
                ModelState.AddModelError("Input.ExtraConfigJson", "Must be valid JSON, or left blank.");
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var now = DateTimeOffset.UtcNow;
        Instance instance;

        if (Input.Id is { } id)
        {
            var existing = await db.Instances.SingleOrDefaultAsync(i => i.Id == id, ct);
            if (existing is null)
            {
                return NotFound();
            }
            instance = existing;
        }
        else
        {
            instance = new Instance { Name = Input.Name, BaseUrl = Input.BaseUrl, CreatedAt = now };
            db.Instances.Add(instance);
        }

        instance.Name = Input.Name;
        instance.SourceType = Input.SourceType;
        instance.BaseUrl = Input.BaseUrl;
        instance.Username = string.IsNullOrWhiteSpace(Input.Username) ? null : Input.Username;
        instance.Enabled = Input.Enabled;
        instance.TimeoutSeconds = Input.TimeoutSeconds;
        instance.VerifySsl = Input.VerifySsl;
        instance.ExtraConfigJson = string.IsNullOrWhiteSpace(Input.ExtraConfigJson) ? null : Input.ExtraConfigJson;
        instance.UpdatedAt = now;

        if (!string.IsNullOrEmpty(Input.Password))
        {
            instance.PasswordProtected = secretProtector.Protect(Input.Password);
        }
        if (!string.IsNullOrEmpty(Input.ApiKey))
        {
            instance.ApiKeyProtected = secretProtector.Protect(Input.ApiKey);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            ModelState.AddModelError("Input.Name", "An instance with this name already exists.");
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
        public SourceType SourceType { get; set; } = SourceType.Qbittorrent;

        [Required]
        [Display(Name = "Base URL")]
        public string BaseUrl { get; set; } = "";

        public string? Username { get; set; }
        public string? Password { get; set; }

        [Display(Name = "API key / token")]
        public string? ApiKey { get; set; }

        public bool Enabled { get; set; } = true;

        [Range(1, 600)]
        [Display(Name = "Timeout (seconds)")]
        public int TimeoutSeconds { get; set; } = 30;

        [Display(Name = "Verify SSL certificate")]
        public bool VerifySsl { get; set; } = true;

        [Display(Name = "Extra config (JSON)")]
        public string? ExtraConfigJson { get; set; }

        public bool HasExistingPassword { get; set; }
        public bool HasExistingApiKey { get; set; }
    }
}
