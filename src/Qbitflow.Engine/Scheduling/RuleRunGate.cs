using System.Collections.Concurrent;

namespace Qbitflow.Engine.Scheduling;

/// <summary>Overlap protection: a rule cannot start again while its previous run is in flight.</summary>
public class RuleRunGate
{
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    /// <summary>Returns true (and marks the rule in-flight) if no run for this rule is currently active.</summary>
    public bool TryEnter(int ruleId) => _inFlight.TryAdd(ruleId, 0);

    public void Exit(int ruleId) => _inFlight.TryRemove(ruleId, out _);

    public bool IsInFlight(int ruleId) => _inFlight.ContainsKey(ruleId);
}
