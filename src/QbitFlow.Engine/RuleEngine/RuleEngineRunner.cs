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
using QbitFlow.Engine.Sources;
using QbitFlow.Infrastructure.Config;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Engine.RuleEngine;

/// <summary>One torrent's verdict for a previewed expression. <c>Matched</c> is null when it errored.</summary>
public sealed record RulePreviewRow(string TorrentName, string Category, string Hash, bool? Matched);

/// <summary>Result of "Test against current torrents" — counts plus a capped sample of rows.</summary>
public sealed record RulePreview(
    string RuleName, string Expression,
    int Evaluated, int Matched, int Errored, IReadOnlyList<RulePreviewRow> Rows);

public interface IRuleEngineRunner
{
    /// <summary>
    /// One engine pass: evaluate every enabled rule (in <see cref="Rule.Order"/>) against a shared
    /// torrent snapshot of every needed qBittorrent instance, firing each rule's action when its
    /// criteria match. Returns the <see cref="RunHistory"/> id, or <c>null</c> when there was nothing
    /// to do (no enabled rules) — in that case no run record is written at all, so an idle install
    /// does not accumulate empty rows.
    /// </summary>
    Task<Guid?> RunAsync(RunTrigger trigger, bool? dryRunOverride, CancellationToken ct);

    /// <summary>
    /// Evaluate-only preview used by the rule editor: runs <paramref name="expression"/> against
    /// current torrents and reports what would match. Never mutates anything and never writes a run.
    /// </summary>
    /// <param name="targetIds">qBittorrent instances to sample; null/empty means every enabled one.</param>
    Task<RulePreview> PreviewRuleAsync(string ruleName, string expression, IReadOnlyList<Guid>? targetIds, int limit, CancellationToken ct);
}

/// <summary>
/// Executes one rule-engine pass. Reads the global <see cref="EngineSettings"/>, plans the enabled
/// rules, pulls each needed qBittorrent instance's torrents through <see cref="TorrentSnapshotCache"/>
/// (so N rules over the same instance cost one fetch), evaluates every rule against every torrent in
/// parallel, and writes a single <see cref="RunHistory"/> with per-rule counts and a log.
/// <para>
/// Media / watch-popularity fields come from the analytics cache via <see cref="IMediaEnricher"/> —
/// a pass never talks to Plex/Jellyfin. Database access uses short-lived contexts from
/// <see cref="IDbContextFactory{TContext}"/> and is never held across the torrent loop.
/// </para>
/// </summary>
public sealed class RuleEngineRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    AppSettingStore settings,
    IQbtGatewayFactory gateways,
    TorrentSnapshotCache snapshots,
    ActionRegistry actions,
    CriteriaEvaluator evaluator,
    EvaluationContextBuilder contextBuilder,
    DriveDataProvider drives,
    RuleCooldownTracker cooldowns,
    IRunLogPublisher runLog,
    ILoggerFactory loggerFactory,
    ILogger<RuleEngineRunner> log) : IRuleEngineRunner
{
    /// <summary>Newest runs retained; older ones (and their logs) are pruned after each pass.</summary>
    private const int RunsToKeep = 200;

    /// <summary>A rule resolved into everything the torrent loop needs, so it is resolved once per pass.</summary>
    private sealed record RulePlan(
        Rule Rule, IActionHandler Handler, IReadOnlyDictionary<string, string> RawParams,
        bool StopOnMatch, int? CooldownSeconds, IReadOnlyList<Guid>? TargetIds);

    /// <summary>Per-rule counters accumulated across the parallel torrent loop; guarded by <c>lock</c>.</summary>
    private sealed class Tally { public int Success, Failure, Error, Applied, WouldApply, Cooldown; }

    public async Task<Guid?> RunAsync(RunTrigger trigger, bool? dryRunOverride, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        EngineSettings cfg;
        RunHistory run;
        bool dryRun;
        List<RulePlan> plans;
        List<Guid> allQbtIds;

        cfg = await settings.GetEngineSettingsAsync(ct);
        dryRun = dryRunOverride ?? cfg.DryRun;

        // Load + open the run in one short-lived context; the torrent loop below opens its own.
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var rules = await db.Rules
                .Include(r => r.Action)
                .Where(r => r.Enabled)
                .OrderBy(r => r.Order)
                .ToListAsync(ct);

            plans = BuildPlans(rules, cfg.StopOnFirstMatch);
            if (plans.Count == 0)
                return null;   // nothing enabled — don't record an empty run

            allQbtIds = await db.SourceConnections
                .Where(s => s.Kind == SourceKind.Qbt && s.Enabled)
                .Select(s => s.Id)
                .ToListAsync(ct);

            run = new RunHistory { Trigger = trigger, DryRun = dryRun };
            db.RunHistory.Add(run);
            await db.SaveChangesAsync(ct);
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
        var maxAge = cfg.Interval;

        try
        {
            Emit(LogLevel.Information, $"Rule engine pass start · trigger={trigger} · dryRun={dryRun} · {plans.Count} rule(s)");

            // A rule with no target filter wants every enabled instance, so one such rule pulls in
            // the whole set; otherwise only the union of the filters (ignoring ids that no longer exist).
            var neededIds = plans.Any(p => p.TargetIds is null)
                ? allQbtIds
                : plans.SelectMany(p => p.TargetIds!).Distinct().Where(allQbtIds.Contains).ToList();

            if (neededIds.Count == 0)
                Emit(LogLevel.Warning, "No enabled qBittorrent sources match the rules — nothing to do.");

            var driveSnapshot = drives.Snapshot();

            foreach (var qbtId in neededIds)
            {
                ct.ThrowIfCancellationRequested();

                IReadOnlyList<TorrentView> torrents;
                try
                {
                    torrents = await snapshots.GetAsync(qbtId, maxAge, ct);
                }
                catch (Exception ex)
                {
                    Emit(LogLevel.Error, $"qBittorrent source {qbtId} unreachable: {ex.Message} — skipping.");
                    errorCount++;
                    continue;
                }

                var target = gateways.GetActionTarget(qbtId);
                var plansHere = plans
                    .Where(p => p.TargetIds is null || p.TargetIds.Contains(qbtId))
                    .ToList();

                Emit(LogLevel.Information, $"Source {qbtId}: {torrents.Count} torrents · {plansHere.Count} rule(s)");
                torrentsEvaluated += torrents.Count;

                await Parallel.ForEachAsync(
                    torrents,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, cfg.MaxParallelism), CancellationToken = ct },
                    async (torrent, token) =>
                        await ProcessTorrentAsync(qbtId, target, torrent, plansHere, driveSnapshot, dryRun, tallies, Emit, token));
            }
        }
        catch (OperationCanceledException)
        {
            status = RunStatus.Cancelled;
            Emit(LogLevel.Warning, "Pass cancelled.");
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed;
            errorCount++;
            Emit(LogLevel.Error, $"Pass failed: {ex.Message}");
            log.LogError(ex, "Rule engine pass {RunId} failed", run.Id);
        }

        sw.Stop();

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var runRow = await db.RunHistory.FirstAsync(r => r.Id == run.Id);

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
                runRow.ActionsSkipped += t.Cooldown;
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
            await db.SaveChangesAsync(CancellationToken.None);
            await PruneOldRunsAsync(db);

            Emit(LogLevel.Information,
                $"Pass done · {runRow.DurationMs} ms · applied={runRow.ActionsApplied} wouldApply={runRow.ActionsWouldApply} errors={runRow.ErrorCount}");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to finalise pass {RunId}; attempting a minimal status write", run.Id);
            await TryMarkFailedAsync(run.Id);
        }

        runLog.Complete(run.Id);
        return run.Id;
    }

    public async Task<RulePreview> PreviewRuleAsync(string ruleName, string expression, IReadOnlyList<Guid>? targetIds, int limit, CancellationToken ct)
    {
        var cfg = await settings.GetEngineSettingsAsync(ct);

        List<Guid> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var all = await db.SourceConnections
                .Where(s => s.Kind == SourceKind.Qbt && s.Enabled).Select(s => s.Id).ToListAsync(ct);
            ids = targetIds is { Count: > 0 } ? all.Where(targetIds.Contains).ToList() : all;
        }

        var drive = drives.Snapshot();
        var maxAge = cfg.Interval;
        var rows = new List<RulePreviewRow>();
        int evaluated = 0, matched = 0, errored = 0;

        foreach (var qbtId in ids)
        {
            IReadOnlyList<TorrentView> torrents;
            try { torrents = await snapshots.GetAsync(qbtId, maxAge, ct); }
            catch { continue; }

            foreach (var t in torrents)
            {
                if (rows.Count >= limit) break;
                evaluated++;
                var fields = await contextBuilder.BuildAsync(qbtId, t, drive, ct);
                var m = evaluator.Evaluate(expression, fields, logContext: $"preview '{ruleName}'");
                if (m == true) matched++;
                else if (m is null) errored++;
                rows.Add(new RulePreviewRow(t.Name, string.IsNullOrEmpty(t.Category) ? "(uncategorized)" : t.Category, t.Hash, m));
            }
        }

        return new RulePreview(ruleName, expression, evaluated, matched, errored, rows);
    }

    /// <summary>
    /// Resolves each enabled rule to its action handler and parsed parameters once, up front. A rule
    /// whose action type is unknown (e.g. removed in an upgrade) is logged and dropped rather than
    /// failing the whole pass.
    /// </summary>
    private List<RulePlan> BuildPlans(IReadOnlyList<Rule> rules, bool globalStop)
    {
        var plans = new List<RulePlan>();
        foreach (var rule in rules)
        {
            if (rule.Action is null || !actions.TryGet(rule.Action.Type, out var handler))
            {
                log.LogWarning("Rule '{Rule}' has no usable action ('{Type}') — skipped.", rule.Name, rule.Action?.Type);
                continue;
            }

            plans.Add(new RulePlan(
                rule, handler, ParseParams(rule.Action.ParamsJson),
                rule.StopOnMatch ?? globalStop,
                rule.CooldownSeconds,
                ParseTargets(rule.TargetFilterJson)));
        }
        return plans;
    }

    /// <summary>
    /// Runs every rule against one torrent, in order. The evaluation context (drive + torrent + media
    /// fields) is built once and reused for all rules. Handlers are still invoked when the criteria do
    /// not match — <c>tag.sync</c> relies on that to remove a tag it previously added — so the outcome,
    /// not the match, decides what is counted as applied.
    /// </summary>
    private async Task ProcessTorrentAsync(
        Guid qbtId,
        IQbtActionTarget target,
        TorrentView torrent,
        IReadOnlyList<RulePlan> plans,
        IReadOnlyDictionary<string, object?> driveSnapshot,
        bool dryRun,
        ConcurrentDictionary<Guid, Tally> tallies,
        Action<LogLevel, string, string?, Guid?> emit,
        CancellationToken ct)
    {
        var fields = await contextBuilder.BuildAsync(qbtId, torrent, driveSnapshot, ct);

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
                else tally.Error++;   // null = the expression threw
            }

            // Cooldown only gates real side effects: a dry run reports what *would* happen, so it
            // neither consumes nor is blocked by the window.
            if (match == true && !dryRun &&
                !cooldowns.TryFire(plan.Rule.Id, torrent.Hash, plan.CooldownSeconds, DateTimeOffset.UtcNow))
            {
                lock (tally) tally.Cooldown++;
                emit(LogLevel.Information, $"'{plan.Rule.Name}' matched '{torrent.Name}' but is in cooldown — skipped", torrent.Hash, plan.Rule.Id);
                if (plan.StopOnMatch) break;
                continue;
            }

            // Action parameters may embed <placeholder> tokens resolved against this torrent.
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

    /// <summary>
    /// Keeps run history bounded at <see cref="RunsToKeep"/>, deleting each stale run's log lines and
    /// per-rule results first. Best-effort: a failure here must never fail an otherwise good pass.
    /// </summary>
    private static async Task PruneOldRunsAsync(AppDbContext db)
    {
        try
        {
            var stale = await db.RunHistory.AsNoTracking()
                .OrderByDescending(r => r.StartedUtc)
                .Skip(RunsToKeep)
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

    /// <summary>
    /// Last resort when finalising a pass threw: at least stop the run showing as <c>Running</c>
    /// forever, so the UI and the cancel endpoint stay truthful.
    /// </summary>
    private async Task TryMarkFailedAsync(Guid runId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var r = await db.RunHistory.FirstOrDefaultAsync(x => x.Id == runId);
            if (r is not null) { r.Status = RunStatus.Failed; r.FinishedUtc = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not mark pass {RunId} failed", runId);
        }
    }

    /// <summary>
    /// Reads <see cref="Rule.TargetFilterJson"/>. Null means "every enabled qBittorrent source", and
    /// unparseable or empty JSON degrades to that rather than silently targeting nothing.
    /// </summary>
    private static IReadOnlyList<Guid>? ParseTargets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var ids = JsonSerializer.Deserialize<List<Guid>>(json);
            return ids is { Count: > 0 } ? ids : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Flattens <see cref="RuleAction.ParamsJson"/> to string values (numbers and booleans stringified)
    /// because every action parameter is placeholder-substituted as text before use.
    /// </summary>
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
