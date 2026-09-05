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
        var (instances, snapshot, _) = await BuildSnapshotAsync(ct);
        using (snapshot)
        {
            var targetInstanceIds = JsonSerializer.Deserialize<List<int>>(rule.TargetInstanceIdsJson) ?? [];
            var matches = await EvaluateAsync(
                snapshot, rule.ConditionTreeJson, rule.UseAdvancedSql, rule.AdvancedSqlWhere, targetInstanceIds, ct);

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
    }

    public async Task<RulePreview> DryRunAsync(RuleDraft draft, CancellationToken ct = default)
    {
        try
        {
            var (instances, snapshot, torrentCount) = await BuildSnapshotAsync(ct);
            using (snapshot)
            {
                var matches = await EvaluateAsync(
                    snapshot, draft.ConditionTreeJson, draft.UseAdvancedSql, draft.AdvancedSqlWhere, draft.TargetInstanceIds, ct);

                var actionDefinitions = JsonSerializer.Deserialize<List<ActionDefinition>>(draft.ActionsJson) ?? [];
                var instancesById = instances.ToDictionary(i => i.Id, ToConnectionInfo);

                // dryRun: true -> no writes; ActionExecutor still reads current state so the
                // "already matches" vs "would change" split is accurate.
                var summary = await actionExecutor.ExecuteAsync(actionDefinitions, instancesById, matches, dryRun: true, ct);

                var lines = actionDefinitions
                    .Select(a => a.GetType().Name)
                    .Distinct()
                    .Select(typeName =>
                    {
                        var forType = summary.Results.Where(r => r.ActionType == typeName).ToList();
                        var def = actionDefinitions.First(a => a.GetType().Name == typeName);
                        return new PreviewActionLine(
                            DescribeAction(def),
                            forType.Count(r => r.Outcome == ActionOutcome.DryRun),
                            forType.Count(r => r.Outcome == ActionOutcome.SkippedAlreadyMatching),
                            forType.Count(r => r.Outcome == ActionOutcome.Failed));
                    })
                    .ToList();

                return new RulePreview(
                    Ok: true,
                    MatchedCount: matches.Count,
                    TorrentsInSnapshot: torrentCount,
                    Actions: lines,
                    SampleMatchedHashes: matches.Select(m => m.TorrentHash).Take(15).ToList(),
                    Error: null);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rule dry-run failed");
            return RulePreview.Failure(ex.Message);
        }
    }

    private async Task<(List<Instance> Instances, SnapshotDatabase Snapshot, int TorrentCount)> BuildSnapshotAsync(CancellationToken ct)
    {
        var instances = await db.Instances.Where(i => i.Enabled).ToListAsync(ct);
        var connections = instances.Select(ToConnectionInfo).ToList();

        await refreshCoordinator.RefreshAsync(connections, forceRefresh: false, ct);

        var pathMappingRules = await db.PathMappingRules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);
        var storagePaths = await db.StoragePaths.AsNoTracking().Where(s => s.Enabled).ToListAsync(ct);

        var snapshot = new SnapshotDatabase();
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
        return (instances, snapshot, input.Torrents.Count);
    }

    private async Task<List<MatchedTorrent>> EvaluateAsync(
        SnapshotDatabase snapshot,
        string conditionTreeJson,
        bool useAdvancedSql,
        string? advancedSqlWhere,
        IReadOnlyList<int> targetInstanceIds,
        CancellationToken ct)
    {
        if (useAdvancedSql && !string.IsNullOrWhiteSpace(advancedSqlWhere))
        {
            var validation = advancedSqlExecutor.Validate(snapshot, advancedSqlWhere, AdvancedSqlMode.WhereClause);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException($"Advanced SQL is invalid: {validation.ErrorMessage}");
            }

            var matches = await advancedSqlExecutor.ExecuteAsync(snapshot, validation.CompiledSql!, ct: ct);
            return targetInstanceIds.Count > 0
                ? matches.Where(m => targetInstanceIds.Contains(m.InstanceId)).ToList()
                : matches;
        }

        var tree = JsonSerializer.Deserialize<ConditionNode>(conditionTreeJson)
            ?? throw new InvalidOperationException("Rule has no condition tree.");
        var compiled = conditionCompiler.Compile(tree, targetInstanceIds.Count > 0 ? targetInstanceIds : null);
        return await conditionCompiler.ExecuteAsync(snapshot, compiled, ct);
    }

    private static string DescribeAction(ActionDefinition action) => action switch
    {
        AddTagsAction a => $"Add tag(s): {string.Join(", ", a.Tags)}",
        RemoveTagsAction a => $"Remove tag(s): {string.Join(", ", a.Tags)}",
        SetCategoryAction a => $"Set category: {a.Category}",
        MoveAction a => $"Move to: {a.DestinationPath}",
        SetUploadLimitAction a => $"Set upload limit: {a.LimitBytesPerSec} B/s",
        SetDownloadLimitAction a => $"Set download limit: {a.LimitBytesPerSec} B/s",
        _ => action.GetType().Name
    };

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
