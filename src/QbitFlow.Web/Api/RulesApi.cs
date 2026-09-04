using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Core.Expressions;
using QbitFlow.Engine.RuleEngine;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Api;

internal static class RulesApi
{
    public static void MapRulesApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/rules/compile", (RuleWriter.GroupDto? group, ConditionCompiler compiler) =>
        {
            var result = compiler.Compile(RuleWriter.ToGroupEntity(group, Guid.Empty));
            return Results.Json(new { expression = result.Expression, valid = result.Valid, errors = result.Errors });
        });

        app.MapGet("/api/rules", async (AppDbContext db) =>
        {
            var rules = await db.Rules.AsNoTracking().Include(r => r.Action)
                .OrderBy(r => r.Order).ToListAsync();
            return Results.Json(rules.Select(Shape));
        });

        app.MapPost("/api/rules/test", async (RuleWriter.RuleDraft dto, IRuleEngineRunner runner, ConditionCompiler compiler, int? limit, CancellationToken ct) =>
        {
            var expr = dto.Mode.Equals("raw", StringComparison.OrdinalIgnoreCase)
                ? dto.RawExpression ?? "true"
                : compiler.Compile(RuleWriter.ToGroupEntity(dto.Group, Guid.Empty)).Expression;

            var report = await runner.PreviewRuleAsync(dto.Name, expr, dto.TargetIds, Math.Clamp(limit ?? 300, 1, 1000), ct);
            return Results.Json(report);
        });
    }

    private static object Shape(Rule r) => new
    {
        r.Id, r.Name, r.Order, r.Enabled, r.StopOnMatch, r.CooldownSeconds, r.TargetFilterJson,
        mode = r.ConditionMode.ToString(), r.RawExpression,
        expression = r.CompiledExpression, r.CompileValid, r.CompileError,
        action = r.Action is null ? null : new { r.Action.Type, r.Action.ParamsJson },
    };
}
