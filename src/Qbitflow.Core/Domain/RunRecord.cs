namespace Qbitflow.Core.Domain;

/// <summary>
/// One execution of a Rule: what ran, what matched, what changed, what failed, how long it took.
/// </summary>
public class RunRecord
{
    public long Id { get; set; }
    public int RuleId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RunOutcome Outcome { get; set; }
    public int MatchedCount { get; set; }
    public int ActionsExecutedCount { get; set; }
    public int ActionsSkippedCount { get; set; }
    public int ActionsFailedCount { get; set; }
    public bool WasDryRun { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Per-item results (matched torrent ids, actions taken/skipped/failed), JSON-encoded.</summary>
    public string? DetailsJson { get; set; }
}
