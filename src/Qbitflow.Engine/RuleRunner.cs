using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.Actions;
using Qbitflow.Core.Domain.Conditions;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Engine.Actions;
using Qbitflow.Engine.Conditions;
using Qbitflow.Engine.Conditions.AdvancedSql;
using Qbitflow.Infrastructure.Persistence;
using Qbitflow.Infrastructure.Security;
using Qbitflow.Snapshot;
using Qbitflow.Sources.Cache;
using Qbitflow.Sources.Coordination;
using Qbitflow.Sources.Storage;

namespace Qbitflow.Engine;

/// <summary>
/// One rule's full run: refresh -> rebuild snapshot -> compile+run condition -> apply
/// actions -> persist a RunRecord. Every enabled instance is refreshed on every run
/// (relying on Qbitflow.Sources' per-source TTL cache to make that cheap when another
/// rule just refreshed the same instance) -- a future phase could narrow this to only
/// the datasets a given rule's condition actually references.
/// </summary>
public class RuleRunner(
    AppDbContext db,
    ISecretProtector secretProtector,
    ISourceRefreshCoordinator refreshCoordinator,
    ISourceDataCache sourceCache,
    IStorageUsageService storageUsageService,
    ConditionSqlCompiler conditionCompiler,
    AdvancedSqlExecutor advancedSqlExecutor,
    IActionExecutor actionExecutor,
    ILogger<RuleRunner> logger) : IRuleRunner
{
    public async Task RunAsync(int ruleId, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var rule = await db.Rules.SingleOrDefaultAsync(r => r.Id == ruleId, ct);
        if (rule is null)
        {
            logger.LogWarning("Rule {RuleId} no longer exists; skipping run", ruleId);
            return;
        }

        var settings = await db.AppSettings.AsNoTracking().SingleAsync(s => s.Id == 1, ct);
        var run = new RunRecord
        {
            RuleId = ruleId,
            StartedAt = startedAt,
            WasDryRun = settings.GlobalDryRun || rule.DryRun
        };

        try
        {
            if (settings.GlobalKillSwitch)
            {
                run.Outcome = RunOutcome.Skipped;
                run.ErrorMessage = "Global kill switch is enabled.";
            }
            else
            {
                await RunCoreAsync(rule, settings, run, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rule {RuleId} run failed", ruleId);
            run.Outcome = RunOutcome.Failed;
            run.ErrorMessage = ex.Message;
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        db.RunRecords.Add(run);
        rule.LastRunAt = run.FinishedAt;
        await db.SaveChangesAsync(ct);
    }

    private async Task RunCoreAsync(Rule rule, AppSettings settings, RunRecord run, CancellationToken ct)
    {
        var instances = await db.Instances.Where(i => i.Enabled).ToListAsync(ct);
        var connections = instances.Select(ToConnectionInfo).ToList();

        await refreshCoordinator.RefreshAsync(connections, forceRefresh: false, ct);

        var pathMappingRules = await db.PathMappingRules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);
        var storagePaths = await db.StoragePaths.AsNoTracking().Where(s => s.Enabled).ToListAsync(ct);

        using var snapshot = new SnapshotDatabase();
        var input = new SnapshotInput
        {
            PathMappingRules = pathMappingRules,
            StoragePaths = storagePaths.Select(storageUsageService.GetUsage).ToList()
        };

        foreach (var connection in connections)
        {
            if (sourceCache.TryGetAny(connection.InstanceId, out var cached) && cached is not null)
            {
                input.Torrents.AddRange(cached.Torrents);
                input.MediaItems.AddRange(cached.MediaItems);
                input.WatchHistory.AddRange(cached.WatchHistory);
            }
        }

        snapshot.Rebuild(input);

        var targetInstanceIds = JsonSerializer.Deserialize<List<int>>(rule.TargetInstanceIdsJson) ?? [];

        List<MatchedTorrent> matches;
        if (rule.UseAdvancedSql && !string.IsNullOrWhiteSpace(rule.AdvancedSqlWhere))
        {
            var validation = advancedSqlExecutor.Validate(snapshot, rule.AdvancedSqlWhere, AdvancedSqlMode.WhereClause);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException($"Advanced SQL is invalid: {validation.ErrorMessage}");
            }

            matches = await advancedSqlExecutor.ExecuteAsync(snapshot, validation.CompiledSql!, ct: ct);
            if (targetInstanceIds.Count > 0)
            {
                matches = matches.Where(m => targetInstanceIds.Contains(m.InstanceId)).ToList();
            }
        }
        else
        {
            var tree = JsonSerializer.Deserialize<ConditionNode>(rule.ConditionTreeJson)
                ?? throw new InvalidOperationException("Rule has no condition tree.");
            var compiled = conditionCompiler.Compile(tree, targetInstanceIds.Count > 0 ? targetInstanceIds : null);
            matches = await conditionCompiler.ExecuteAsync(snapshot, compiled, ct);
        }

        run.MatchedCount = matches.Count;

        var actionDefinitions = JsonSerializer.Deserialize<List<ActionDefinition>>(rule.ActionsJson) ?? [];
        var instancesById = instances.ToDictionary(i => i.Id, ToConnectionInfo);
        var effectiveDryRun = settings.GlobalDryRun || rule.DryRun;

        var summary = await actionExecutor.ExecuteAsync(actionDefinitions, instancesById, matches, effectiveDryRun, ct);

        run.ActionsExecutedCount = summary.AppliedCount;
        run.ActionsSkippedCount = summary.SkippedCount + summary.DryRunCount;
        run.ActionsFailedCount = summary.FailedCount;
        run.Outcome = summary.FailedCount > 0 ? RunOutcome.PartialFailure : RunOutcome.Success;
        run.DetailsJson = JsonSerializer.Serialize(summary.Results);
    }

    private SourceConnectionInfo ToConnectionInfo(Instance i) => new()
    {
        InstanceId = i.Id,
        InstanceName = i.Name,
        SourceType = i.SourceType,
        BaseUrl = i.BaseUrl,
        ApiKey = i.ApiKeyProtected is null ? null : secretProtector.Unprotect(i.ApiKeyProtected),
        Username = i.Username,
        Password = i.PasswordProtected is null ? null : secretProtector.Unprotect(i.PasswordProtected),
        TimeoutSeconds = i.TimeoutSeconds,
        VerifySsl = i.VerifySsl,
        ExtraConfigJson = i.ExtraConfigJson
    };
}
