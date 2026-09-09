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

        // Export writes a file per torrent and isolates failures per hash, so it can't go
        // through the single batched ApplyToClientAsync call the other actions share.
        if (action is ExportTorrentAction exportAction)
        {
            results.AddRange(await ExportTorrentsAsync(connection, exportAction, toApply, currentState, ct));
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
        StartTorrentAction => !IsStopped(state.State),
        StopTorrentAction => IsStopped(state.State),
        ExportTorrentAction a => ExportAlreadyOnDisk(a, state),
        _ => false
    };

    // qBittorrent reports a stopped torrent as "stoppedUP"/"stoppedDL" (5.0+) or the older
    // "pausedUP"/"pausedDL"; every other state ("downloading", "queuedUP", "checkingDL", ...)
    // counts as running for start/stop idempotency.
    private static bool IsStopped(string? state) =>
        state is not null &&
        (state.StartsWith("stopped", StringComparison.OrdinalIgnoreCase) ||
         state.StartsWith("paused", StringComparison.OrdinalIgnoreCase));

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
            case StartTorrentAction:
                await qbtClient.StartTorrentsAsync(connection, hashes, ct);
                break;
            case StopTorrentAction:
                await qbtClient.StopTorrentsAsync(connection, hashes, ct);
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

    // .Trim() keeps the idempotency check and the post-move poll agreeing with the adapter,
    // which trims the destination before handing it to qBittorrent.
    private static string NormalizePath(string? path) => (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private async Task<List<ActionResult>> ExportTorrentsAsync(
        SourceConnectionInfo connection,
        ExportTorrentAction action,
        List<string> hashes,
        IReadOnlyDictionary<string, QbtTorrentState> currentState,
        CancellationToken ct)
    {
        var results = new List<ActionResult>();

        foreach (var hash in hashes)
        {
            currentState.TryGetValue(hash, out var state);
            try
            {
                var bytes = await qbtClient.ExportTorrentAsync(connection, hash, ct);
                var directory = ExportDirectory(action, state);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, ExportFileName(hash, state?.Name));
                await File.WriteAllBytesAsync(path, bytes, ct);
                results.Add(new ActionResult { InstanceId = connection.InstanceId, TorrentHash = hash, ActionType = nameof(ExportTorrentAction), Outcome = ActionOutcome.Applied });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Exporting torrent {Hash} to {Destination} failed", hash, action.DestinationPath);
                results.Add(new ActionResult { InstanceId = connection.InstanceId, TorrentHash = hash, ActionType = nameof(ExportTorrentAction), Outcome = ActionOutcome.Failed, Error = ex.Message });
            }
        }

        return results;
    }

    private static bool ExportAlreadyOnDisk(ExportTorrentAction action, QbtTorrentState state)
    {
        var directory = ExportDirectory(action, state);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        // The hash suffix is the identity, not the (mutable) torrent name. "[" and "]" are
        // literal in a search pattern (.NET only expands "*" and "?"), and the infohash is hex.
        return Directory.EnumerateFiles(directory, $"*[{state.Hash}].torrent").Any()
            || File.Exists(Path.Combine(directory, $"{state.Hash}.torrent"));
    }

    private static string ExportDirectory(ExportTorrentAction action, QbtTorrentState? state)
    {
        var root = action.DestinationPath.Trim();
        return action.Layout == TorrentExportLayout.PerCategory && !string.IsNullOrWhiteSpace(state?.Category)
            ? Path.Combine(root, SanitizeSegment(state.Category!))
            : root;
    }

    private static string ExportFileName(string hash, string? torrentName) =>
        string.IsNullOrWhiteSpace(torrentName)
            ? $"{hash}.torrent"
            : $"{SanitizeSegment(torrentName)} [{hash}].torrent";

    // A fixed set the export files can be written on Linux and still opened on Windows,
    // rather than Path.GetInvalidFileNameChars() which is much smaller on Linux. Control
    // chars and the Windows-reserved punctuation become "_"; length is capped so
    // "<name> [<40-char hash>].torrent" stays well inside the filesystem's limit.
    private static readonly char[] ReservedNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static string SanitizeSegment(string value)
    {
        var cleaned = new string(value
            .Select(c => c < ' ' || ReservedNameChars.Contains(c) ? '_' : c)
            .ToArray()).Trim();
        return cleaned.Length > 150 ? cleaned[..150].TrimEnd() : cleaned;
    }

    private static ActionResult Failure(MatchedTorrent m, ActionDefinition action, string error) => new()
    {
        InstanceId = m.InstanceId,
        TorrentHash = m.TorrentHash,
        ActionType = action.GetType().Name,
        Outcome = ActionOutcome.Failed,
        Error = error
    };
}
