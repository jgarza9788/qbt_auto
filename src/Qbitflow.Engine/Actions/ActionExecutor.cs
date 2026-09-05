using Microsoft.Extensions.Logging;
using Qbitflow.Core.Domain.Actions;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Core.Interfaces;
using Qbitflow.Engine.Conditions;

namespace Qbitflow.Engine.Actions;

public class ActionExecutor(
    IQbtActionClient qbtClient,
    ILogger<ActionExecutor> logger,
    TimeSpan? movePollInterval = null,
    int moveMaxAttempts = 15) : IActionExecutor
{
    private readonly TimeSpan _movePollInterval = movePollInterval ?? TimeSpan.FromSeconds(2);


    public async Task<ActionExecutionSummary> ExecuteAsync(
        IReadOnlyList<ActionDefinition> actions,
        IReadOnlyDictionary<int, SourceConnectionInfo> instancesById,
        IReadOnlyList<MatchedTorrent> matches,
        bool dryRun,
        CancellationToken ct = default)
    {
        var results = new List<ActionResult>();

        foreach (var group in matches.GroupBy(m => m.InstanceId))
        {
            if (!instancesById.TryGetValue(group.Key, out var connection))
            {
                foreach (var m in group)
                foreach (var action in actions)
                {
                    results.Add(Failure(m, action, "Target instance not found or not enabled."));
                }
                continue;
            }

            var hashes = group.Select(m => m.TorrentHash).Distinct().ToList();

            Dictionary<string, QbtTorrentState> currentState;
            try
            {
                currentState = await qbtClient.GetCurrentStateAsync(connection, hashes, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read current state for instance {InstanceName}", connection.InstanceName);
                foreach (var m in group)
                foreach (var action in actions)
                {
                    results.Add(Failure(m, action, $"Could not read current state: {ex.Message}"));
                }
                continue;
            }

            foreach (var action in actions)
            {
                results.AddRange(await ApplyActionAsync(connection, action, hashes, currentState, dryRun, ct));
            }
        }

        return new ActionExecutionSummary { Results = results };
    }

    private async Task<List<ActionResult>> ApplyActionAsync(
        SourceConnectionInfo connection,
        ActionDefinition action,
        IReadOnlyList<string> hashes,
        IReadOnlyDictionary<string, QbtTorrentState> currentState,
        bool dryRun,
        CancellationToken ct)
    {
        var typeName = action.GetType().Name;
        var results = new List<ActionResult>();
        var toApply = new List<string>();

        foreach (var hash in hashes)
        {
            var alreadyApplied = currentState.TryGetValue(hash, out var state) && IsAlreadyApplied(action, state);
            if (alreadyApplied)
            {
                results.Add(new ActionResult { InstanceId = connection.InstanceId, TorrentHash = hash, ActionType = typeName, Outcome = ActionOutcome.SkippedAlreadyMatching });
            }
            else
            {
                toApply.Add(hash);
            }
        }

        if (toApply.Count == 0)
        {
            return results;
        }

        if (dryRun)
        {
            results.AddRange(toApply.Select(h => new ActionResult { InstanceId = connection.InstanceId, TorrentHash = h, ActionType = typeName, Outcome = ActionOutcome.DryRun }));
            return results;
        }

        try
        {
            await ApplyToClientAsync(connection, action, toApply, ct);
            results.AddRange(toApply.Select(h => new ActionResult { InstanceId = connection.InstanceId, TorrentHash = h, ActionType = typeName, Outcome = ActionOutcome.Applied }));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Action {ActionType} failed for instance {InstanceName}", typeName, connection.InstanceName);
            results.AddRange(toApply.Select(h => new ActionResult { InstanceId = connection.InstanceId, TorrentHash = h, ActionType = typeName, Outcome = ActionOutcome.Failed, Error = ex.Message }));
        }

        return results;
    }

    private static bool IsAlreadyApplied(ActionDefinition action, QbtTorrentState state) => action switch
    {
        AddTagsAction a => a.Tags.All(t => state.Tags.Contains(t)),
        RemoveTagsAction a => a.Tags.All(t => !state.Tags.Contains(t)),
        SetCategoryAction a => string.Equals(state.Category, a.Category, StringComparison.Ordinal),
        SetUploadLimitAction a => state.UploadLimitBytesPerSec == a.LimitBytesPerSec,
        SetDownloadLimitAction a => state.DownloadLimitBytesPerSec == a.LimitBytesPerSec,
        MoveAction a => NormalizePath(state.SavePath) == NormalizePath(a.DestinationPath),
        _ => false
    };

    private async Task ApplyToClientAsync(SourceConnectionInfo connection, ActionDefinition action, List<string> hashes, CancellationToken ct)
    {
        switch (action)
        {
            case AddTagsAction a:
                await qbtClient.AddTagsAsync(connection, hashes, a.Tags, ct);
                break;
            case RemoveTagsAction a:
                await qbtClient.RemoveTagsAsync(connection, hashes, a.Tags, ct);
                break;
            case SetCategoryAction a:
                await qbtClient.SetCategoryAsync(connection, hashes, a.Category, ct);
                break;
            case SetUploadLimitAction a:
                await qbtClient.SetUploadLimitAsync(connection, hashes, a.LimitBytesPerSec, ct);
                break;
            case SetDownloadLimitAction a:
                await qbtClient.SetDownloadLimitAsync(connection, hashes, a.LimitBytesPerSec, ct);
                break;
            case MoveAction a:
                await qbtClient.SetLocationAsync(connection, hashes, a.DestinationPath, ct);
                if (a.WaitForCompletion)
                {
                    await WaitForMoveAsync(connection, hashes, a.DestinationPath, ct);
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported action type '{action.GetType().Name}'.");
        }
    }

    private async Task WaitForMoveAsync(SourceConnectionInfo connection, List<string> hashes, string destination, CancellationToken ct)
    {
        for (var attempt = 0; attempt < moveMaxAttempts; attempt++)
        {
            await Task.Delay(_movePollInterval, ct);
            var state = await qbtClient.GetCurrentStateAsync(connection, hashes, ct);
            if (hashes.All(h => state.TryGetValue(h, out var s) && NormalizePath(s.SavePath) == NormalizePath(destination)))
            {
                return;
            }
        }

        throw new TimeoutException("Move did not complete within the expected time.");
    }

    private static string NormalizePath(string? path) => (path ?? string.Empty).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static ActionResult Failure(MatchedTorrent m, ActionDefinition action, string error) => new()
    {
        InstanceId = m.InstanceId,
        TorrentHash = m.TorrentHash,
        ActionType = action.GetType().Name,
        Outcome = ActionOutcome.Failed,
        Error = error
    };
}
