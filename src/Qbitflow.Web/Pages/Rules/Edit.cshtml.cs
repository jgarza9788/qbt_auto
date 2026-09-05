using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Cronos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.Actions;
using Qbitflow.Core.Domain.Conditions;
using Qbitflow.Engine.Conditions;
using Qbitflow.Engine.Conditions.AdvancedSql;
using Qbitflow.Engine.Scheduling;
using Qbitflow.Infrastructure.Persistence;
using Qbitflow.Snapshot;

namespace Qbitflow.Web.Pages.Rules;

public class EditModel(AppDbContext db, ConditionSqlCompiler conditionCompiler, AdvancedSqlExecutor advancedSqlExecutor) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsNew => Input.Id is null;
    public List<Instance> QbtInstances { get; private set; } = [];
    public List<string> StoragePathNames { get; private set; } = [];

    public string FieldRegistryJson => JsonSerializer.Serialize(BuildFieldRegistryPayload());
    public string StoragePathNamesJson => JsonSerializer.Serialize(StoragePathNames);
    public string SchedulePresetsJson => JsonSerializer.Serialize(
        CommonSchedulePresets.Presets.Select(p => new { label = p.Label, cron = p.CronExpression }));
    public IReadOnlyList<(string Signature, string Description)> UdfHelpers => SnapshotFieldRegistry.Helpers;

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken ct)
    {
        if (id is not null)
        {
            var rule = await db.Rules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
            if (rule is null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Id = rule.Id,
                Name = rule.Name,
                Description = rule.Description,
                Enabled = rule.Enabled,
                Priority = rule.Priority,
                StopOnMatch = rule.StopOnMatch,
                DryRun = rule.DryRun,
                CronExpression = rule.CronExpression,
                TimeZoneId = rule.TimeZoneId,
                TargetInstanceIds = JsonSerializer.Deserialize<List<int>>(rule.TargetInstanceIdsJson) ?? [],
                UseAdvancedSql = rule.UseAdvancedSql,
                AdvancedSqlWhere = rule.AdvancedSqlWhere,
                ConditionTreeJson = rule.ConditionTreeJson,
                ActionsJson = rule.ActionsJson
            };
        }

        await LoadReferenceDataAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var cronResult = CronValidator.Validate(Input.CronExpression, Input.TimeZoneId);
        if (!cronResult.IsValid)
        {
            ModelState.AddModelError("Input.CronExpression", cronResult.ErrorMessage!);
        }

        if (Input.UseAdvancedSql)
        {
            if (string.IsNullOrWhiteSpace(Input.AdvancedSqlWhere))
            {
                ModelState.AddModelError("Input.AdvancedSqlWhere", "Advanced SQL cannot be empty.");
            }
            else
            {
                using var snapshot = new SnapshotDatabase();
                var validation = advancedSqlExecutor.Validate(snapshot, Input.AdvancedSqlWhere, AdvancedSqlMode.WhereClause);
                if (!validation.IsValid)
                {
                    ModelState.AddModelError("Input.AdvancedSqlWhere", validation.ErrorMessage!);
                }
            }
        }
        else
        {
            try
            {
                var tree = JsonSerializer.Deserialize<ConditionNode>(Input.ConditionTreeJson)
                    ?? throw new InvalidOperationException("Condition cannot be empty.");
                conditionCompiler.Compile(tree);
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("Input.ConditionTreeJson", $"Condition is invalid: {ex.Message}");
            }
        }

        List<ActionDefinition>? actions = null;
        try
        {
            actions = JsonSerializer.Deserialize<List<ActionDefinition>>(Input.ActionsJson);
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("Input.ActionsJson", $"Actions are invalid: {ex.Message}");
        }
        if (actions is null or { Count: 0 })
        {
            ModelState.AddModelError("Input.ActionsJson", "At least one action is required.");
        }

        if (Input.TargetInstanceIds.Count == 0)
        {
            ModelState.AddModelError("Input.TargetInstanceIds", "Select at least one target qBittorrent instance.");
        }

        if (!ModelState.IsValid)
        {
            await LoadReferenceDataAsync(ct);
            return Page();
        }

        var now = DateTimeOffset.UtcNow;
        Rule rule;
        if (Input.Id is { } id)
        {
            var existing = await db.Rules.SingleOrDefaultAsync(r => r.Id == id, ct);
            if (existing is null)
            {
                return NotFound();
            }
            rule = existing;
        }
        else
        {
            rule = new Rule
            {
                Name = Input.Name,
                CronExpression = Input.CronExpression,
                ConditionTreeJson = Input.ConditionTreeJson,
                ActionsJson = Input.ActionsJson,
                TargetInstanceIdsJson = "[]",
                CreatedAt = now
            };
            db.Rules.Add(rule);
        }

        rule.Name = Input.Name;
        rule.Description = Input.Description;
        rule.Enabled = Input.Enabled;
        rule.Priority = Input.Priority;
        rule.StopOnMatch = Input.StopOnMatch;
        rule.DryRun = Input.DryRun;
        rule.CronExpression = Input.CronExpression;
        rule.TimeZoneId = Input.TimeZoneId;
        rule.UseAdvancedSql = Input.UseAdvancedSql;
        rule.AdvancedSqlWhere = Input.AdvancedSqlWhere;
        rule.ConditionTreeJson = Input.ConditionTreeJson;
        rule.ActionsJson = Input.ActionsJson;
        rule.TargetInstanceIdsJson = JsonSerializer.Serialize(Input.TargetInstanceIds);
        rule.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return RedirectToPage("/Rules/Index");
    }

    public IActionResult OnPostPreviewCondition()
    {
        try
        {
            var tree = JsonSerializer.Deserialize<ConditionNode>(Input.ConditionTreeJson)
                ?? throw new InvalidOperationException("Condition is empty.");
            var targetIds = Input.TargetInstanceIds.Count > 0 ? Input.TargetInstanceIds : null;
            var compiled = conditionCompiler.Compile(tree, targetIds);
            return Content(compiled.Sql, "text/plain");
        }
        catch (Exception ex)
        {
            Response.StatusCode = 400;
            return Content($"Invalid condition: {ex.Message}", "text/plain");
        }
    }

    public IActionResult OnPostValidateAdvancedSql()
    {
        using var snapshot = new SnapshotDatabase();
        var validation = advancedSqlExecutor.Validate(snapshot, Input.AdvancedSqlWhere ?? "", AdvancedSqlMode.WhereClause);
        if (!validation.IsValid)
        {
            Response.StatusCode = 400;
            return Content($"Invalid: {validation.ErrorMessage}", "text/plain");
        }
        return Content(validation.CompiledSql!, "text/plain");
    }

    public IActionResult OnPostPreviewSchedule()
    {
        var validation = CronValidator.Validate(Input.CronExpression, Input.TimeZoneId);
        if (!validation.IsValid)
        {
            Response.StatusCode = 400;
            return Content(validation.ErrorMessage!, "text/plain");
        }

        var description = CronDescriptionService.Describe(Input.CronExpression);
        var cron = CronExpression.Parse(Input.CronExpression, CronFormat.Standard);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(Input.TimeZoneId);
        var nextRuns = CronValidator.GetNextOccurrences(cron, tz, DateTimeOffset.UtcNow, 3);

        var lines = new List<string> { description, "" };
        lines.AddRange(nextRuns.Select(r => $"  next: {TimeZoneInfo.ConvertTime(r, tz):yyyy-MM-dd HH:mm} ({Input.TimeZoneId})"));
        return Content(string.Join("\n", lines), "text/plain");
    }

    private async Task LoadReferenceDataAsync(CancellationToken ct)
    {
        QbtInstances = await db.Instances.AsNoTracking()
            .Where(i => i.SourceType == SourceType.Qbittorrent)
            .OrderBy(i => i.Name)
            .ToListAsync(ct);
        StoragePathNames = await db.StoragePaths.AsNoTracking().OrderBy(s => s.Name).Select(s => s.Name).ToListAsync(ct);
    }

    private static Dictionary<string, object> BuildFieldRegistryPayload()
    {
        var result = new Dictionary<string, object>();
        foreach (var (relation, def) in SnapshotFieldRegistry.Relations)
        {
            result[relation] = def.Fields.Values
                .Select(f => new { key = f.Key, valueType = f.ValueType.ToString(), description = f.Description, example = f.ExampleValue })
                .ToList();
        }
        result["__storage"] = SnapshotFieldRegistry.StorageAttributes
            .Select(kv => new { key = kv.Key, valueType = kv.Value.ValueType.ToString(), description = (string?)null })
            .ToList();
        return result;
    }

    public class InputModel
    {
        public int? Id { get; set; }

        [Required]
        public string Name { get; set; } = "";

        public string? Description { get; set; }
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; }

        [Display(Name = "Stop processing lower-priority rules for a matched torrent")]
        public bool StopOnMatch { get; set; }

        [Display(Name = "Dry-run (preview only for this rule)")]
        public bool DryRun { get; set; }

        [Required]
        [Display(Name = "Cron expression")]
        public string CronExpression { get; set; } = "0 3 * * *";

        [Required]
        public string TimeZoneId { get; set; } = "UTC";

        [Display(Name = "Target qBittorrent instances")]
        public List<int> TargetInstanceIds { get; set; } = [];

        public bool UseAdvancedSql { get; set; }
        public string? AdvancedSqlWhere { get; set; }
        public string ConditionTreeJson { get; set; } = """{"kind":"group","Operator":"And","Children":[]}""";
        public string ActionsJson { get; set; } = "[]";
    }
}
