namespace Qbitflow.Engine.Actions;

public class ActionResult
{
    public required int InstanceId { get; init; }
    public required string TorrentHash { get; init; }
    public required string ActionType { get; init; }
    public required ActionOutcome Outcome { get; init; }
    public string? Error { get; init; }
}

public class ActionExecutionSummary
{
    public List<ActionResult> Results { get; init; } = [];

    public int AppliedCount => Results.Count(r => r.Outcome == ActionOutcome.Applied);
    public int SkippedCount => Results.Count(r => r.Outcome == ActionOutcome.SkippedAlreadyMatching);
    public int DryRunCount => Results.Count(r => r.Outcome == ActionOutcome.DryRun);
    public int FailedCount => Results.Count(r => r.Outcome == ActionOutcome.Failed);
}
