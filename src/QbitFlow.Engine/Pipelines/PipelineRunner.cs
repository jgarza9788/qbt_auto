using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;
using QbitFlow.Core.Domain;
using QbitFlow.Core.Expressions;
using QbitFlow.Engine.Actions;
using QbitFlow.Engine.Derived;
using QbitFlow.Engine.Evaluation;
using QbitFlow.Engine.Scheduling;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Engine.Pipelines;

public sealed record RulePreviewRow(string TorrentName, string Category, string Hash, bool? Matched);

public sealed record RulePreview(
    Guid PipelineId, string RuleName, string Expression,
    int Evaluated, int Matched, int Errored, IReadOnlyList<RulePreviewRow> Rows);

public interface IPipelineRunner
{
    Task<Guid> RunAsync(Guid pipelineId, RunTrigger trigger, bool? dryRunOverride, CancellationToken ct);

    /// <summary>Evaluate-only: runs an expression against the pipeline's current torrents, no mutations, no run record.</summary>
    Task<RulePreview> PreviewRuleAsync(Guid pipelineId, string ruleName, string expression, int limit, CancellationToken ct);
}

/// <summary>
/// Runs one pipeline cycle: refresh the qBittorrent target(s) it needs → for every torrent evaluate
/// every enabled rule in order and fire its event when the criteria are true (log-only under
/// dry-run) → write the run summary + logs. Media / hot-cold fields come from the analytics cache
/// via <see cref="IMediaEnricher"/> — a pipeline run never talks to Plex/Jellyfin directly.
/// DB access uses short-lived contexts (never held across the torrent loop).
/// </summary>
public sealed class PipelineRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    IQbtGatewayFactory gateways,
    ActionRegistry actions,
    CriteriaEvaluator evaluator,
    EvaluationContextBuilder contextBuilder,
    DriveDataProvider drives,
    IRunLogPublisher runLog,
    ILoggerFactory loggerFactory,
    ILogger<PipelineRunner> log) : IPipelineRunner
{
    private sealed record RulePlan(Rule Rule, IActionHandler Handler, IReadOnlyDictionary<string, string> RawParams, bool StopOnMatch);

    private sealed class Tally
    {
        public int Success, Failure, Error, Applied, WouldApply;
    }

    public async Task<Guid> RunAsync(Guid pipelineId, RunTrigger trigger, bool? dryRunOverride, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // ---- load + open the run (short context) ----
        Pipeline pipeline;
        RunHistory run;
        bool dryRun;
        List<RulePlan> plans;
        List<Guid> targetIds;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            pipeline = await db.Pipelines
                .Include(p => p.Sources)
                .Include(p => p.Rules).ThenInclude(r => r.Action)
                .FirstOrDefaultAsync(p => p.Id == pipelineId, ct)
                ?? throw new KeyNotFoundException($"Pipeline {pipelineId} not found.");

            dryRun = dryRunOverride ?? pipeline.DryRun;
            run = new RunHistory { PipelineId = pipeline.Id, Trigger = trigger, DryRun = dryRun };
            db.RunHistory.Add(run);

            var tracked = await db.Pipelines.FirstAsync(p => p.Id == pipelineId, ct);
            tracked.IsRunning = true;
            await db.SaveChangesAsync(ct);

            targetIds = pipeline.Sources
                .Where(s => s.Roles.HasFlag(PipelineSourceRoles.ActionTarget))
                .Select(s => s.SourceConnectionId)
                .Distinct()
                .ToList();

            plans = BuildPlans(pipeline);
        }

        var runLogger = loggerFactory.CreateLogger($"Run:{run.Id:N}");
        var logSink = new ConcurrentQueue<RunLogEntry>();
        long seq = 0;
        void Emit(LogLevel level, string message, string? hash = null, Guid? ruleId = null)
        {
            runLogger.Log(level, "{Message}", message);
            var entry = new RunLogEntry
            {
                RunId = run.Id,
                Seq = Interlocked.Increment(ref seq),
                Level = level.ToString(),
                Message = message,
                TorrentHash = hash,
                RuleId = ruleId,
            };
            logSink.Enqueue(entry);
            runLog.Publish(run.Id, entry);
        }

        var tallies = new ConcurrentDictionary<Guid, Tally>();
        var status = RunStatus.Succeeded;
        var torrentsEvaluated = 0;
        var errorCount = 0;

        try
        {
            Emit(LogLevel.Information, $"Pipeline '{pipeline.Name}' start · trigger={trigger} · dryRun={dryRun}");

            if (targetIds.Count == 0)
                Emit(LogLevel.Warning, "No qBittorrent action targets configured — nothing to do.");

            var driveSnapshot = drives.Snapshot();

            foreach (var targetId in targetIds)
            {
                ct.ThrowIfCancellationRequested();

                IReadOnlyList<TorrentView> torrents;
                var adapter = gateways.GetAdapter(targetId);
                var target = gateways.GetActionTarget(targetId);
                try
                {
                    torrents = await adapter.FetchTorrentsAsync(ct);
                }
                catch (Exception ex)
                {
                    Emit(LogLevel.Error, $"qBittorrent target {targetId} unreachable: {ex.Message} — skipping.");
                    errorCount++;
                    continue;
                }

                Emit(LogLevel.Information, $"Target {targetId}: {torrents.Count} torrents");
                torrentsEvaluated += torrents.Count;

                await Parallel.ForEachAsync(
                    torrents,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, pipeline.MaxParallelism), CancellationToken = ct },
                    async (torrent, token) =>
                        await ProcessTorrentAsync(targetId, target, torrent, plans, driveSnapshot, dryRun, tallies, Emit, token));
            }
        }
        catch (OperationCanceledException)
        {
            status = RunStatus.Cancelled;
            Emit(LogLevel.Warning, "Run cancelled.");
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed;
            errorCount++;
            Emit(LogLevel.Error, $"Run failed: {ex.Message}");
            log.LogError(ex, "Pipeline run {RunId} failed", run.Id);
        }

        sw.Stop();

        // ---- finalise (fresh short context; must not throw the run into limbo) ----
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);

            var runRow = await db.RunHistory.FirstAsync(r => r.Id == run.Id);
            var ruleIds = plans.Select(p => p.Rule.Id).ToHashSet();

            foreach (var plan in plans)
            {
                var t = tallies.GetValueOrDefault(plan.Rule.Id) ?? new Tally();
                db.RunRuleResults.Add(new RunRuleResult
                {
                    RunId = run.Id,
                    RuleId = plan.Rule.Id,
                    RuleName = plan.Rule.Name,
                    SuccessCount = t.Success,
                    FailureCount = t.Failure,
                    ErrorCount = t.Error,
                    ActionsApplied = t.Applied,
                    ActionsWouldApply = t.WouldApply,
                });
                runRow.ActionsApplied += t.Applied;
                runRow.ActionsWouldApply += t.WouldApply;
                errorCount += t.Error;
            }

            runRow.Status = status;
            runRow.TorrentsEvaluated = torrentsEvaluated;
            runRow.RulesEvaluated = plans.Count;
            runRow.ErrorCount = errorCount;
            runRow.FinishedUtc = DateTimeOffset.UtcNow;
            runRow.DurationMs = (long)sw.Elapsed.TotalMilliseconds;
            runRow.SummaryJson = JsonSerializer.Serialize(new
            {
                runRow.TorrentsEvaluated, runRow.RulesEvaluated, runRow.ActionsApplied,
                runRow.ActionsWouldApply, runRow.ActionsSkipped, runRow.ErrorCount,
            });

            db.RunLogEntries.AddRange(logSink);

            var pipelineRow = await db.Pipelines.FirstAsync(p => p.Id == pipelineId);
            pipelineRow.IsRunning = false;
            pipelineRow.LastRunUtc = runRow.FinishedUtc;
            pipelineRow.LastRunId = run.Id;
            pipelineRow.NextRunUtc = Schedule.Next(pipelineRow, runRow.FinishedUtc.Value);

            await db.SaveChangesAsync(CancellationToken.None);
            await PruneOldRunsAsync(db, pipelineId, keep: 50);

            Emit(LogLevel.Information,
                $"Pipeline '{pipeline.Name}' done · {runRow.DurationMs} ms · applied={runRow.ActionsApplied} wouldApply={runRow.ActionsWouldApply} errors={runRow.ErrorCount}");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to finalise run {RunId}; attempting a minimal status write", run.Id);
            await TryMarkFailedAsync(run.Id, pipelineId);
        }

        runLog.Complete(run.Id);
        return run.Id;
    }

    public async Task<RulePreview> PreviewRuleAsync(Guid pipelineId, string ruleName, string expression, int limit, CancellationToken ct)
    {
        Pipeline pipeline;
        List<Guid> targetIds;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            pipeline = await db.Pipelines.Include(p => p.Sources)
                .FirstOrDefaultAsync(p => p.Id == pipelineId, ct)
                ?? throw new KeyNotFoundException($"Pipeline {pipelineId} not found.");
            targetIds = pipeline.Sources
                .Where(s => s.Roles.HasFlag(PipelineSourceRoles.ActionTarget))
                .Select(s => s.SourceConnectionId).Distinct().ToList();
        }

        var drive = drives.Snapshot();
        var rows = new List<RulePreviewRow>();
        int evaluated = 0, matched = 0, errored = 0;

        foreach (var targetId in targetIds)
        {
            IReadOnlyList<TorrentView> torrents;
            try { torrents = await gateways.GetAdapter(targetId).FetchTorrentsAsync(ct); }
            catch { continue; }

            foreach (var t in torrents)
            {
                if (rows.Count >= limit) break;
                evaluated++;
                var fields = await contextBuilder.BuildAsync(targetId, t, drive, ct);
                var m = evaluator.Evaluate(expression, fields, logContext: $"preview '{ruleName}'");
                if (m == true) matched++;
                else if (m is null) errored++;
                rows.Add(new RulePreviewRow(t.Name, string.IsNullOrEmpty(t.Category) ? "(uncategorized)" : t.Category, t.Hash, m));
            }
        }

        return new RulePreview(pipelineId, ruleName, expression, evaluated, matched, errored, rows);
    }

    private static async Task PruneOldRunsAsync(AppDbContext db, Guid pipelineId, int keep)
    {
        try
        {
            var stale = await db.RunHistory.AsNoTracking()
                .Where(r => r.PipelineId == pipelineId)
                .OrderByDescending(r => r.StartedUtc)
                .Skip(keep)
                .Select(r => r.Id)
                .ToListAsync();

            if (stale.Count == 0) return;

            await db.RunLogEntries.Where(e => stale.Contains(e.RunId)).ExecuteDeleteAsync();
            await db.RunRuleResults.Where(x => stale.Contains(x.RunId)).ExecuteDeleteAsync();
            await db.RunHistory.Where(r => stale.Contains(r.Id)).ExecuteDeleteAsync();
        }
        catch
        {
            // pruning is best-effort
        }
    }

    private async Task TryMarkFailedAsync(Guid runId, Guid pipelineId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var r = await db.RunHistory.FirstOrDefaultAsync(x => x.Id == runId);
            if (r is not null) { r.Status = RunStatus.Failed; r.FinishedUtc = DateTimeOffset.UtcNow; }
            var p = await db.Pipelines.FirstOrDefaultAsync(x => x.Id == pipelineId);
            if (p is not null) p.IsRunning = false;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not mark run {RunId} failed", runId);
        }
    }

    private List<RulePlan> BuildPlans(Pipeline pipeline)
    {
        var plans = new List<RulePlan>();
        foreach (var rule in pipeline.Rules.Where(r => r.Enabled).OrderBy(r => r.Order))
        {
            if (rule.Action is null || !actions.TryGet(rule.Action.Type, out var handler))
            {
                log.LogWarning("Rule '{Rule}' has no usable action ('{Type}') — skipped.", rule.Name, rule.Action?.Type);
                continue;
            }

            var raw = ParseParams(rule.Action.ParamsJson);
            plans.Add(new RulePlan(rule, handler, raw, rule.StopOnMatch ?? pipeline.StopOnFirstMatch));
        }
        return plans;
    }

    private async Task ProcessTorrentAsync(
        Guid targetId,
        IQbtActionTarget target,
        TorrentView torrent,
        IReadOnlyList<RulePlan> plans,
        IReadOnlyDictionary<string, object?> driveSnapshot,
        bool dryRun,
        ConcurrentDictionary<Guid, Tally> tallies,
        Action<LogLevel, string, string?, Guid?> emit,
        CancellationToken ct)
    {
        var fields = await contextBuilder.BuildAsync(targetId, torrent, driveSnapshot, ct);

        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            var tally = tallies.GetOrAdd(plan.Rule.Id, _ => new Tally());

            var match = evaluator.Evaluate(plan.Rule.CompiledExpression, fields,
                logContext: $"rule='{plan.Rule.Name}' torrent='{torrent.Name}'");

            lock (tally)
            {
                if (match == true) tally.Success++;
                else if (match == false) tally.Failure++;
                else tally.Error++;
            }

            var substitutedParams = plan.RawParams.ToDictionary(
                kv => kv.Key,
                kv => PlaceholderReplacer.Apply(kv.Value, fields),
                StringComparer.Ordinal);

            var actionCtx = new ActionContext
            {
                RuleId = plan.Rule.Id,
                RuleName = plan.Rule.Name,
                Match = match,
                Torrent = torrent,
                Fields = fields,
                Params = substitutedParams,
                Qbt = target,
                DryRun = dryRun,
                Log = loggerFactory.CreateLogger($"Action:{plan.Handler.Type}"),
                CancellationToken = ct,
            };

            ActionOutcome outcome;
            try
            {
                outcome = await plan.Handler.ApplyAsync(actionCtx);
            }
            catch (Exception ex)
            {
                lock (tally) tally.Error++;
                emit(LogLevel.Error, $"Action {plan.Handler.Type} threw on '{torrent.Name}': {ex.Message}", torrent.Hash, plan.Rule.Id);
                continue;
            }

            switch (outcome)
            {
                case ActionOutcome.Applied:
                    lock (tally) tally.Applied++;
                    emit(LogLevel.Information, $"{plan.Handler.Type} applied to '{torrent.Name}'", torrent.Hash, plan.Rule.Id);
                    break;
                case ActionOutcome.WouldApply:
                    lock (tally) tally.WouldApply++;
                    emit(LogLevel.Information, $"[dry-run] {plan.Handler.Type} would apply to '{torrent.Name}'", torrent.Hash, plan.Rule.Id);
                    break;
                case ActionOutcome.Error:
                    lock (tally) tally.Error++;
                    emit(LogLevel.Warning, $"{plan.Handler.Type} reported an error on '{torrent.Name}'", torrent.Hash, plan.Rule.Id);
                    break;
            }

            if (match == true && plan.StopOnMatch)
                break;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseParams(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
            return doc.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.ToString(),
                StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
