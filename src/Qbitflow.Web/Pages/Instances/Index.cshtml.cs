using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Infrastructure.Persistence;
using Qbitflow.Infrastructure.Security;
using Qbitflow.Sources.Adapters;

namespace Qbitflow.Web.Pages.Instances;

public class IndexModel(
    AppDbContext db,
    ISecretProtector secretProtector,
    ISourceAdapterResolver adapterResolver,
    ILogger<IndexModel> logger) : PageModel
{
    public List<Instance> Instances { get; private set; } = [];
    public List<StoragePathConfig> StoragePaths { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Instances = await db.Instances.AsNoTracking().OrderBy(i => i.Name).ToListAsync(ct);
        StoragePaths = await db.StoragePaths.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostTestConnectionAsync(int id, CancellationToken ct)
    {
        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);
        if (instance is null)
        {
            return NotFound();
        }

        var connection = new SourceConnectionInfo
        {
            InstanceId = instance.Id,
            InstanceName = instance.Name,
            SourceType = instance.SourceType,
            BaseUrl = instance.BaseUrl,
            ApiKey = instance.ApiKeyProtected is null ? null : secretProtector.Unprotect(instance.ApiKeyProtected),
            Username = instance.Username,
            Password = instance.PasswordProtected is null ? null : secretProtector.Unprotect(instance.PasswordProtected),
            TimeoutSeconds = instance.TimeoutSeconds,
            VerifySsl = instance.VerifySsl,
            ExtraConfigJson = instance.ExtraConfigJson
        };

        logger.LogInformation(
            "Connection test requested for instance {InstanceId} '{InstanceName}' ({SourceType})",
            instance.Id, instance.Name, instance.SourceType);

        var adapter = adapterResolver.Resolve(instance.SourceType);
        var result = await adapter.TestConnectionAsync(connection, ct);

        if (result.Success)
        {
            logger.LogInformation(
                "Connection test OK for '{InstanceName}': {Message}", instance.Name, result.Message);
        }
        else
        {
            logger.LogWarning(
                "Connection test FAILED for '{InstanceName}': {Message}", instance.Name, result.Message);
        }

        return Partial("_ConnectionTestResult", result);
    }

    public async Task<IActionResult> OnPostToggleEnabledAsync(int id, CancellationToken ct)
    {
        var instance = await db.Instances.FindAsync([id], ct);
        if (instance is not null)
        {
            instance.Enabled = !instance.Enabled;
            instance.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteInstanceAsync(int id, CancellationToken ct)
    {
        var instance = await db.Instances.FindAsync([id], ct);
        if (instance is not null)
        {
            db.Instances.Remove(instance);
            await db.SaveChangesAsync(ct);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleStoragePathAsync(int id, CancellationToken ct)
    {
        var path = await db.StoragePaths.FindAsync([id], ct);
        if (path is not null)
        {
            path.Enabled = !path.Enabled;
            path.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteStoragePathAsync(int id, CancellationToken ct)
    {
        var path = await db.StoragePaths.FindAsync([id], ct);
        if (path is not null)
        {
            db.StoragePaths.Remove(path);
            await db.SaveChangesAsync(ct);
        }
        return RedirectToPage();
    }
}
