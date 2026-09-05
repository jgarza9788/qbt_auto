using Qbitflow.Core.Domain.Actions;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Engine.Conditions;

namespace Qbitflow.Engine.Actions;

public interface IActionExecutor
{
    /// <summary>
    /// Applies every action to every matched torrent, batched per instance. Idempotent:
    /// a torrent already in an action's desired state is skipped, never reapplied.
    /// dryRun previews exactly what would happen (per-torrent) without calling qBittorrent.
    /// </summary>
    Task<ActionExecutionSummary> ExecuteAsync(
        IReadOnlyList<ActionDefinition> actions,
        IReadOnlyDictionary<int, SourceConnectionInfo> instancesById,
        IReadOnlyList<MatchedTorrent> matches,
        bool dryRun,
        CancellationToken ct = default);
}
