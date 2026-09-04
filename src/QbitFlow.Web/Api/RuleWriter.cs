using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Core.Expressions;
using QbitFlow.Engine.RuleEngine;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Api;

/// <summary>
/// Persists the single global rule list from an editor payload in one pass. Owns the rule-mutation
/// logic that used to live behind the per-rule <c>/api/rules/*</c> endpoints; the <c>/Rules</c> page
/// handler calls <see cref="ReconcileAsync"/> and owns the transaction (this class never calls
/// <c>SaveChangesAsync</c>), so a save is all-or-nothing.
/// </summary>
public sealed class RuleWriter(AppDbContext db, ConditionCompiler compiler, RuleCooldownTracker cooldowns)
{
    public sealed record CondDto(string Field, string Operator, string ValueKind, string Value);

    public sealed record GroupDto(string Logic, List<CondDto>? Conditions, List<GroupDto>? Children);

    /// <summary>
    /// One row from the editor. <see cref="Id"/> is null for a rule the user just added;
    /// <see cref="Order"/> is ignored on input — payload position wins.
    /// </summary>
    public sealed record RuleDraft(
        Guid? Id,
        string Name, int Order, bool Enabled, bool? StopOnMatch,
        string Mode, string? RawExpression, GroupDto? Group,
        string ActionType, string ActionParamsJson,
        IReadOnlyList<Guid>? TargetIds = null, int? CooldownSeconds = null);

    /// <summary>
    /// Makes the global rule list match <paramref name="drafts"/> exactly: updates rules whose
    /// <see cref="RuleDraft.Id"/> matches, inserts the rest, and deletes any existing rule absent
    /// from the payload. Payload index becomes <see cref="Rule.Order"/>, which is how drag-to-reorder
    /// is persisted.
    /// </summary>
    public async Task ReconcileAsync(IReadOnlyList<RuleDraft> drafts, CancellationToken ct)
    {
        var existing = await db.Rules
            .Include(r => r.Action)
            .Include(r => r.RootGroup)
            .ToListAsync(ct);

        var keptIds = new HashSet<Guid>();

        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            var rule = draft.Id is { } id ? existing.FirstOrDefault(r => r.Id == id) : null;

            if (rule is null)
            {
                // Build the rule fully *before* Add: mutating navigations on an already-tracked
                // entity makes EF emit a phantom UPDATE and throw DbUpdateConcurrencyException.
                rule = new Rule();
                ApplyRule(rule, draft with { Order = i }, compiler);
                db.Rules.Add(rule);
            }
            else
            {
                keptIds.Add(rule.Id);

                // The builder tree is replaced wholesale rather than diffed — cheaper and avoids
                // orphaned condition rows.
                if (rule.RootGroupId is { } oldRoot)
                    await DeleteGroupTreeAsync(oldRoot, ct);
                rule.RootGroupId = null;
                rule.RootGroup = null;
                ApplyRule(rule, draft with { Order = i }, compiler);

                // An edited rule should take effect next pass, not sit out its old cooldown window.
                cooldowns.Forget(rule.Id);
            }
        }

        foreach (var orphan in existing.Where(r => !keptIds.Contains(r.Id)))
        {
            if (orphan.RootGroupId is { } root) await DeleteGroupTreeAsync(root, ct);
            db.Rules.Remove(orphan);
            cooldowns.Forget(orphan.Id);
        }
    }

    private static void ApplyRule(Rule rule, RuleDraft dto, ConditionCompiler compiler)
    {
        rule.Name = dto.Name.Trim();
        rule.Order = dto.Order;
        rule.Enabled = dto.Enabled;
        rule.StopOnMatch = dto.StopOnMatch;
        rule.CooldownSeconds = dto.CooldownSeconds is > 0 ? dto.CooldownSeconds : null;
        rule.TargetFilterJson = dto.TargetIds is { Count: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(dto.TargetIds)
            : null;

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

    /// <summary>
    /// Materialises the editor's condition tree into entities. Also used (with an empty rule id) by
    /// the live-compile and rule-test endpoints, which need the tree but never persist it.
    /// </summary>
    public static RuleConditionGroup? ToGroupEntity(GroupDto? dto, Guid ruleId, RuleConditionGroup? parent = null)
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

    /// <summary>
    /// Removes a condition-group tree depth-first. Groups model their own parent/child links rather
    /// than relying on a cascade (SQLite won't cascade a self-reference), so the walk is explicit.
    /// </summary>
    private async Task DeleteGroupTreeAsync(Guid rootGroupId, CancellationToken ct)
    {
        var groups = await db.RuleConditionGroups.Where(g => g.RuleId != Guid.Empty).ToListAsync(ct);
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
}
