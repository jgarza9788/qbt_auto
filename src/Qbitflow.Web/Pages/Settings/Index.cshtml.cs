using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Infrastructure.Config;
using Qbitflow.Infrastructure.Persistence;
using Qbitflow.Web.Logging;

namespace Qbitflow.Web.Pages.Settings;

public class IndexModel(AppDbContext db, IConfigPortabilityService configService) : PageModel
{
    [BindProperty]
    public SettingsInput Input { get; set; } = new();

    public List<PathMappingRule> PathMappingRules { get; private set; } = [];

    public string? ImportError { get; set; }
    public string? ImportSuccess { get; set; }
    public bool SettingsSaved { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var settings = await db.AppSettings.AsNoTracking().SingleAsync(s => s.Id == 1, ct);
        Input = new SettingsInput
        {
            ParallelismLevel = settings.ParallelismLevel,
            GlobalDryRun = settings.GlobalDryRun,
            GlobalKillSwitch = settings.GlobalKillSwitch,
            Theme = settings.Theme,
            LogLevel = settings.LogLevel,
            TimeZoneId = settings.TimeZoneId
        };
        PathMappingRules = await db.PathMappingRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostSaveSettingsAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        var settings = await db.AppSettings.SingleAsync(s => s.Id == 1, ct);
        settings.ParallelismLevel = Input.ParallelismLevel;
        settings.GlobalDryRun = Input.GlobalDryRun;
        settings.GlobalKillSwitch = Input.GlobalKillSwitch;
        settings.Theme = Input.Theme;
        settings.LogLevel = Input.LogLevel;
        settings.TimeZoneId = Input.TimeZoneId;
        await db.SaveChangesAsync(ct);

        // Apply the new log level to the running NLog config immediately (no restart).
        NLogSetup.ApplyMinLevel(NLogSetup.MapLevel(settings.LogLevel));

        SettingsSaved = true;
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostTogglePathMappingAsync(int id, CancellationToken ct)
    {
        var rule = await db.PathMappingRules.FindAsync([id], ct);
        if (rule is not null)
        {
            rule.Enabled = !rule.Enabled;
            await db.SaveChangesAsync(ct);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeletePathMappingAsync(int id, CancellationToken ct)
    {
        var rule = await db.PathMappingRules.FindAsync([id], ct);
        if (rule is not null)
        {
            db.PathMappingRules.Remove(rule);
            await db.SaveChangesAsync(ct);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetExportConfigAsync(ConfigFormat format, CancellationToken ct)
    {
        var content = await configService.ExportConfigAsync(format, ct);
        var ext = format == ConfigFormat.Json ? "json" : "yaml";
        return File(Encoding.UTF8.GetBytes(content), "application/octet-stream", $"qbitflow-config.{ext}");
    }

    public async Task<IActionResult> OnGetExportRulesAsync(ConfigFormat format, CancellationToken ct)
    {
        var content = await configService.ExportRulesAsync(format, ct);
        var ext = format == ConfigFormat.Json ? "json" : "yaml";
        return File(Encoding.UTF8.GetBytes(content), "application/octet-stream", $"qbitflow-rules.{ext}");
    }

    public async Task<IActionResult> OnPostImportConfigAsync(IFormFile? importFile, ConfigFormat importFormat, string importKind, CancellationToken ct)
    {
        if (importFile is null || importFile.Length == 0)
        {
            ImportError = "Choose a file to import.";
            await LoadAsync(ct);
            return Page();
        }

        using var reader = new StreamReader(importFile.OpenReadStream());
        var content = await reader.ReadToEndAsync(ct);

        try
        {
            if (importKind == "rules")
            {
                await configService.ImportRulesAsync(content, importFormat, ct);
                ImportSuccess = "Rules imported.";
            }
            else
            {
                await configService.ImportConfigAsync(content, importFormat, ct);
                ImportSuccess = "Config imported.";
            }
        }
        catch (Exception ex)
        {
            ImportError = $"Import failed: {ex.Message}";
        }

        await LoadAsync(ct);
        return Page();
    }

    public class SettingsInput
    {
        [System.ComponentModel.DataAnnotations.Display(Name = "Parallelism level")]
        public ParallelismLevel ParallelismLevel { get; set; } = ParallelismLevel.Medium;

        [System.ComponentModel.DataAnnotations.Display(Name = "Global dry-run (preview only, apply no actions anywhere)")]
        public bool GlobalDryRun { get; set; }

        [System.ComponentModel.DataAnnotations.Display(Name = "Global kill switch (stop all rules from running)")]
        public bool GlobalKillSwitch { get; set; }

        public string Theme { get; set; } = "system";

        [System.ComponentModel.DataAnnotations.Display(Name = "Log level")]
        public string LogLevel { get; set; } = "Information";

        [System.ComponentModel.DataAnnotations.Display(Name = "Timezone")]
        public string TimeZoneId { get; set; } = "UTC";
    }
}
