using Qbitflow.Engine.Scheduling;
using Xunit;

namespace Qbitflow.Tests.Engine;

public class RuleRunGateTests
{
    [Fact]
    public void TryEnter_SucceedsForANewRule()
    {
        var gate = new RuleRunGate();
        Assert.True(gate.TryEnter(1));
        Assert.True(gate.IsInFlight(1));
    }

    [Fact]
    public void TryEnter_FailsWhileAlreadyInFlight()
    {
        var gate = new RuleRunGate();
        Assert.True(gate.TryEnter(1));
        Assert.False(gate.TryEnter(1));
    }

    [Fact]
    public void Exit_ReleasesTheRule_AllowingReentry()
    {
        var gate = new RuleRunGate();
        gate.TryEnter(1);
        gate.Exit(1);

        Assert.False(gate.IsInFlight(1));
        Assert.True(gate.TryEnter(1));
    }

    [Fact]
    public void DifferentRules_DoNotBlockEachOther()
    {
        var gate = new RuleRunGate();
        Assert.True(gate.TryEnter(1));
        Assert.True(gate.TryEnter(2));
    }
}
