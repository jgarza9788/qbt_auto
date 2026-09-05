namespace Qbitflow.Engine.Actions;

public enum ActionOutcome
{
    Applied,
    SkippedAlreadyMatching,
    DryRun,
    Failed
}
