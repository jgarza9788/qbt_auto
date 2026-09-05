namespace Qbitflow.Engine;

public interface IRuleRunner
{
    /// <summary>Refreshes needed sources, rebuilds a snapshot, evaluates the rule's condition, applies its actions, and persists a RunRecord. Never throws -- any failure is captured as a Failed RunRecord.</summary>
    Task RunAsync(int ruleId, CancellationToken ct = default);
}
