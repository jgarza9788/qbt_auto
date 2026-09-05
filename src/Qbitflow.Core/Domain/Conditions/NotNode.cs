namespace Qbitflow.Core.Domain.Conditions;

public class NotNode : ConditionNode
{
    public required ConditionNode Child { get; init; }
}
