namespace Qbitflow.Core.Domain.Conditions;

/// <summary>
/// EXISTS / NOT EXISTS over a related table (watch_history, media_items, play_counts),
/// correlated back to the outer torrent by path_key. E.g. "no watch history in the last
/// 90 days" is Relation="watch_history", Negate=true, Condition=(days_since_watched &lt;= 90).
/// </summary>
public class ExistsNode : ConditionNode
{
    public required string Relation { get; init; }
    public required ConditionNode Condition { get; init; }
    public bool Negate { get; init; }
}
