using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Sources;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Pages;

public class SourcesModel(AppDbContext db, ISecretProtector secrets, SourceAdapterFactory adapters) : PageModel
{
    public List<SourceConnection> Sources { get; private set; } = [];

    [BindProperty] public Guid? Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string Kind { get; set; } = "Qbt";
    [BindProperty] public string BaseUrl { get; set; } = "";
    [BindProperty] public bool Enabled { get; set; } = true;
    [BindProperty] public string AuthMode { get; set; } = "UserPassword";
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Secret { get; set; }
    [BindProperty] public string? OptionsJson { get; set; } = "{}";

    public async Task OnGetAsync(Guid? edit)
    {
        Sources = await db.SourceConnections.AsNoTracking().OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync();
        if (edit is { } id && Sources.FirstOrDefault(s => s.Id == id) is { } s)
        {
            Id = s.Id; Name = s.Name; Kind = s.Kind.ToString(); BaseUrl = s.BaseUrl; Enabled = s.Enabled;
            AuthMode = s.AuthMode.ToString(); Username = s.Username; OptionsJson = s.OptionsJson;
        }
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var s = Id is { } id ? await db.SourceConnections.FirstOrDefaultAsync(x => x.Id == id) ?? new() : new SourceConnection();
        var isNew = s.Id == Guid.Empty || Id is null;

        s.Name = Name.Trim();
        s.Kind = Enum.Parse<SourceKind>(Kind, true);
        s.BaseUrl = BaseUrl.Trim();
        s.Enabled = Enabled;
        s.AuthMode = Enum.TryParse<SourceAuthMode>(AuthMode, true, out var am) ? am : SourceAuthMode.UserPassword;
        s.Username = Username;
        s.OptionsJson = string.IsNullOrWhiteSpace(OptionsJson) ? "{}" : OptionsJson!;
        s.UpdatedUtc = DateTimeOffset.UtcNow;

        if (!string.IsNullOrEmpty(Secret))
        {
            var (c, n) = secrets.Protect(Secret!);
            s.SecretCiphertext = c; s.SecretNonce = n;
        }

        if (isNew) db.SourceConnections.Add(s);
        await db.SaveChangesAsync();
        adapters.Invalidate(s.Id);
        TempData["Msg"] = isNew ? "Source added." : "Source updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        if (await db.SourceConnections.FirstOrDefaultAsync(x => x.Id == id) is { } s)
        {
            db.SourceConnections.Remove(s);
            await db.SaveChangesAsync();
            adapters.Invalidate(id);
        }
        TempData["Msg"] = "Source deleted.";
        return RedirectToPage();
    }
}
