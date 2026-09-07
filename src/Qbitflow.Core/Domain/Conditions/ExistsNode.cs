namespace Qbitflow.Core.Domain.Conditions;

/// <summary>
/// EXISTS / NOT EXISTS over one related source, correlated back to the outer torrent by path_key.
/// <see cref="Source"/> is a two-segment reference -- "tautulli.tautulli1", or "jellyfin.*" for any
/// instance of that type -- and every field key inside <see cref="Condition"/> must name that same
/// source.
///
/// A single comparison on a related source does not need this node: the compiler correlates a
/// top-level comparison automatically. What this node adds is that several conditions must hold on
/// the <em>same</em> row, which EXISTS(a AND b) means and EXISTS(a) AND EXISTS(b) does not.
/// </summary>
public class ExistsNode : ConditionNode
{
    /// <summary>"&lt;type&gt;.&lt;instance&gt;", e.g. "tautulli.tautulli1" or "jellyfin.*".</summary>
    public required string Source { get; init; }

    public required ConditionNode Condition { get; init; }
    public bool Negate { get; init; }
}
