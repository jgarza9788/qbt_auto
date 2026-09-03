using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Core.Expressions;
using QbitFlow.Engine.Actions;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Pages;

public class RulesModel(AppDbContext db, ActionRegistry actions) : PageModel
{
    public Pipeline Pipeline { get; private set; } = new();
    public List<Rule> Rules { get; private set; } = [];
    public string FieldsJson { get; private set; } = "[]";
    public string ActionsJson { get; private set; } = "[]";
    public string RulesJson { get; private set; } = "[]";

    public async Task<IActionResult> OnGetAsync(Guid pipelineId)
    {
        var p = await db.Pipelines.FirstOrDefaultAsync(x => x.Id == pipelineId);
        if (p is null) return NotFound();
        Pipeline = p;

        Rules = await db.Rules.AsNoTracking()
            .Include(r => r.Action)
            .Include(r => r.RootGroup).ThenInclude(g => g!.Conditions)
            .Where(r => r.PipelineId == pipelineId)
            .OrderBy(r => r.Order)
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

        RulesJson = JsonSerializer.Serialize(Rules.Select(r => new
        {
            r.Id, r.Name, r.Order, r.Enabled, r.StopOnMatch,
            mode = r.ConditionMode.ToString(),
            r.RawExpression, r.CompiledExpression, r.CompileValid, r.CompileError,
            actionType = r.Action?.Type ?? "tag.sync",
            actionParamsJson = r.Action?.ParamsJson ?? "{}",
            group = r.RootGroup is null ? null : SerializeGroup(r.RootGroup),
        }));

        return Page();
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
