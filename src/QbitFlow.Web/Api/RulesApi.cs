using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Core.Expressions;
using QbitFlow.Engine.Pipelines;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Api;

internal static class RulesApi
{
    public sealed record CondDto(string Field, string Operator, string ValueKind, string Value);
    public sealed record GroupDto(string Logic, List<CondDto>? Conditions, List<GroupDto>? Children);

    public sealed record RuleDto(
        string Name, int Order, bool Enabled, bool? StopOnMatch,
        string Mode, string? RawExpression, GroupDto? Group,
        string ActionType, string ActionParamsJson);

    public sealed record ReorderDto(List<Guid> OrderedRuleIds);

    public static void MapRulesApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/rules/compile", (GroupDto? group, ConditionCompiler compiler) =>
        {
            var result = compiler.Compile(ToGroupEntity(group, Guid.Empty));
            return Results.Json(new { expression = result.Expression, valid = result.Valid, errors = result.Errors });
        });

        app.MapGet("/api/pipelines/{pipelineId:guid}/rules", async (Guid pipelineId, AppDbContext db) =>
        {
            var rules = await db.Rules.AsNoTracking().Include(r => r.Action)
                .Where(r => r.PipelineId == pipelineId).OrderBy(r => r.Order).ToListAsync();
            return Results.Json(rules.Select(Shape));
        });

        app.MapPost("/api/pipelines/{pipelineId:guid}/rules", async (Guid pipelineId, RuleDto dto, AppDbContext db, ConditionCompiler compiler) =>
        {
            if (!await db.Pipelines.AnyAsync(p => p.Id == pipelineId)) return Results.NotFound();
            var rule = new Rule { PipelineId = pipelineId };
            ApplyRule(rule, dto, compiler);
            db.Rules.Add(rule);
            await db.SaveChangesAsync();
            return Results.Created($"/api/rules/{rule.Id}", Shape(rule));
        });

        app.MapPut("/api/rules/{id:guid}", async (Guid id, RuleDto dto, AppDbContext db, ConditionCompiler compiler) =>
        {
            var rule = await db.Rules.Include(r => r.Action).FirstOrDefaultAsync(r => r.Id == id);
            if (rule is null) return Results.NotFound();

            // wipe any previous builder tree
            if (rule.RootGroupId is { } oldRoot)
                await DeleteGroupTreeAsync(db, oldRoot);
            rule.RootGroupId = null;

            ApplyRule(rule, dto, compiler);
            await db.SaveChangesAsync();
            return Results.Json(Shape(rule));
        });

        app.MapDelete("/api/rules/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id);
            if (rule is null) return Results.NotFound();
            if (rule.RootGroupId is { } root) await DeleteGroupTreeAsync(db, root);
            db.Rules.Remove(rule);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/pipelines/{pipelineId:guid}/rules/reorder", async (Guid pipelineId, ReorderDto dto, AppDbContext db) =>
        {
            var rules = await db.Rules.Where(r => r.PipelineId == pipelineId).ToListAsync();
            var order = 0;
            foreach (var ruleId in dto.OrderedRuleIds)
                if (rules.FirstOrDefault(r => r.Id == ruleId) is { } r)
                    r.Order = order++;
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/pipelines/{pipelineId:guid}/test-rule", async (Guid pipelineId, RuleDto dto, IPipelineRunner runner, ConditionCompiler compiler, int? limit, CancellationToken ct) =>
        {
            var expr = dto.Mode.Equals("raw", StringComparison.OrdinalIgnoreCase)
                ? dto.RawExpression ?? "true"
                : compiler.Compile(ToGroupEntity(dto.Group, Guid.Empty)).Expression;

            var report = await runner.PreviewRuleAsync(pipelineId, dto.Name, expr, Math.Clamp(limit ?? 300, 1, 1000), ct);
            return Results.Json(report);
        });
    }

    private static void ApplyRule(Rule rule, RuleDto dto, ConditionCompiler compiler)
    {
        rule.Name = dto.Name.Trim();
        rule.Order = dto.Order;
        rule.Enabled = dto.Enabled;
        rule.StopOnMatch = dto.StopOnMatch;

        var builder = !dto.Mode.Equals("raw", StringComparison.OrdinalIgnoreCase);
        rule.ConditionMode = builder ? RuleConditionMode.Builder : RuleConditionMode.Raw;

        if (builder)
        {
            var root = ToGroupEntity(dto.Group, rule.Id);
            var result = compiler.Compile(root);
            rule.RootGroup = root;
            rule.RootGroupId = root?.Id;
            rule.RawExpression = null;
            rule.CompiledExpression = result.Expression;
            rule.CompileValid = result.Valid;
            rule.CompileError = result.Errors.Count > 0 ? string.Join("; ", result.Errors) : null;
        }
        else
        {
            var raw = string.IsNullOrWhiteSpace(dto.RawExpression) ? "true" : dto.RawExpression!.Trim();
            var (ok, error) = new CriteriaEvaluator().Validate(raw);
            rule.RawExpression = raw;
            rule.CompiledExpression = raw;
            rule.CompileValid = ok;
            rule.CompileError = ok ? null : error;
        }

        rule.CompiledUtc = DateTimeOffset.UtcNow;

        rule.Action ??= new RuleAction { RuleId = rule.Id };
        rule.Action.Type = dto.ActionType.Trim();
        rule.Action.ParamsJson = string.IsNullOrWhiteSpace(dto.ActionParamsJson) ? "{}" : dto.ActionParamsJson;
    }

    private static RuleConditionGroup? ToGroupEntity(GroupDto? dto, Guid ruleId, RuleConditionGroup? parent = null)
    {
        if (dto is null) return null;

        var group = new RuleConditionGroup
        {
            RuleId = ruleId,
            ParentGroupId = parent?.Id,
            Logic = Enum.TryParse<ConditionLogic>(dto.Logic, true, out var l) ? l : ConditionLogic.And,
        };

        var order = 0;
        foreach (var c in dto.Conditions ?? [])
            group.Conditions.Add(new RuleCondition
            {
                GroupId = group.Id,
                Field = c.Field,
                Operator = Enum.TryParse<ConditionOperator>(c.Operator, true, out var op) ? op : ConditionOperator.Eq,
                ValueKind = Enum.TryParse<ConditionValueKind>(c.ValueKind, true, out var vk) ? vk : ConditionValueKind.String,
                Value = c.Value ?? "",
                Order = order++,
            });

        var childOrder = 0;
        foreach (var childDto in dto.Children ?? [])
            if (ToGroupEntity(childDto, ruleId, group) is { } child)
            {
                child.Order = childOrder++;
                group.Children.Add(child);
            }

        return group;
    }

    private static async Task DeleteGroupTreeAsync(AppDbContext db, Guid rootGroupId)
    {
        var groups = await db.RuleConditionGroups.Where(g => g.RuleId != Guid.Empty).ToListAsync();
        var toDelete = new List<RuleConditionGroup>();
        void Collect(Guid id)
        {
            var g = groups.FirstOrDefault(x => x.Id == id);
            if (g is null) return;
            toDelete.Add(g);
            foreach (var child in groups.Where(x => x.ParentGroupId == id)) Collect(child.Id);
        }
        Collect(rootGroupId);

        var groupIds = toDelete.Select(g => g.Id).ToHashSet();
        db.RuleConditions.RemoveRange(db.RuleConditions.Where(c => groupIds.Contains(c.GroupId)));
        db.RuleConditionGroups.RemoveRange(toDelete);
    }

    private static object Shape(Rule r) => new
    {
        r.Id, r.PipelineId, r.Name, r.Order, r.Enabled, r.StopOnMatch,
        mode = r.ConditionMode.ToString(), r.RawExpression,
        expression = r.CompiledExpression, r.CompileValid, r.CompileError,
        action = r.Action is null ? null : new { r.Action.Type, r.Action.ParamsJson },
    };
}
