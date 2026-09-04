using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Core.Expressions;
using QbitFlow.Engine.Actions;
using QbitFlow.Infrastructure.Data;
using QbitFlow.Web.Api;

namespace QbitFlow.Web.Pages;

/// <summary>
/// The single global rule list. One page, two sections: the ordered rule table and the rule editor.
/// Rules are edited client-side against a draft model and persisted from <see cref="RulesPayload"/>
/// via <see cref="RuleWriter"/> in one transaction. No pipeline, no schedule — cadence lives in
/// Settings.
/// </summary>
public class RulesModel(AppDbContext db, ActionRegistry actions, RuleWriter ruleWriter) : PageModel
{
    public record QbtSource(Guid Id, string Name);

    public List<QbtSource> QbtSources { get; private set; } = [];
    public string FieldsJson { get; private set; } = "[]";
    public string ActionsJson { get; private set; } = "[]";
    public string RulesJson { get; private set; } = "[]";

    public static IReadOnlyList<FieldDef> Fields => FieldCatalog.All;

    [BindProperty] public string RulesPayload { get; set; } = "[]";

    public async Task OnGetAsync()
    {
        await LoadEditorDataAsync();
        RulesJson = await SerializeRulesAsync();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        List<RuleWriter.RuleDraft>? drafts;
        try
        {
            drafts = JsonSerializer.Deserialize<List<RuleWriter.RuleDraft>>(
                string.IsNullOrWhiteSpace(RulesPayload) ? "[]" : RulesPayload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        }
        catch (JsonException)
        {
            ModelState.AddModelError(nameof(RulesPayload), "The rule list could not be read. Reload the page and try again.");
            drafts = null;
        }

        if (!ModelState.IsValid || drafts is null)
        {
            await LoadEditorDataAsync();
            RulesJson = string.IsNullOrWhiteSpace(RulesPayload) ? "[]" : RulesPayload;
            return Page();
        }

        await ruleWriter.ReconcileAsync(drafts, ct);
        await db.SaveChangesAsync(ct);

        TempData["Msg"] = "Rules saved.";
        return RedirectToPage();
    }

    private async Task LoadEditorDataAsync()
    {
        QbtSources = await db.SourceConnections.AsNoTracking()
            .Where(s => s.Kind == SourceKind.Qbt)
            .OrderBy(s => s.Name)
            .Select(s => new QbtSource(s.Id, s.Name))
            .ToListAsync();

        FieldsJson = JsonSerializer.Serialize(FieldCatalog.All.Select(f => new
        {
            f.Key, type = f.Type.ToString(), source = f.Source.ToString(), f.Description,
        }));

        ActionsJson = JsonSerializer.Serialize(actions.All.Select(a => new
        {
            a.Type, name = a.Schema.DisplayName,
            @params = a.Schema.Params.Select(x => new { x.Key, x.Label, x.Kind, x.Required, x.Help }),
        }).OrderBy(a => a.Type));
    }

    private async Task<string> SerializeRulesAsync()
    {
        var rules = await db.Rules.AsNoTracking()
            .Include(r => r.Action)
            .Include(r => r.RootGroup).ThenInclude(g => g!.Conditions)
            .OrderBy(r => r.Order)
            .ToListAsync();

        return JsonSerializer.Serialize(rules.Select(r => new
        {
            r.Id, r.Name, r.Order, r.Enabled, r.StopOnMatch, r.CooldownSeconds,
            targetIds = ParseTargets(r.TargetFilterJson),
            mode = r.ConditionMode.ToString(),
            r.RawExpression, r.CompiledExpression, r.CompileValid, r.CompileError,
            actionType = r.Action?.Type ?? "tag.sync",
            actionParamsJson = r.Action?.ParamsJson ?? "{}",
            group = r.RootGroup is null ? null : SerializeGroup(r.RootGroup),
        }));
    }

    private static List<Guid> ParseTargets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch { return []; }
    }

    private static object SerializeGroup(RuleConditionGroup g) => new
    {
        logic = g.Logic.ToString(),
        conditions = g.Conditions.OrderBy(c => c.Order).Select(c => new
        {
            field = c.Field, op = c.Operator.ToString(), valueKind = c.ValueKind.ToString(), value = c.Value,
        }),
    };
}
