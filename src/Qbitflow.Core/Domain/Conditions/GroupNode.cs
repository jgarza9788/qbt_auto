namespace Qbitflow.Core.Domain.Conditions;

/// <summary>AND/OR over a list of nested nodes. An empty group is vacuously true for AND, false for OR.</summary>
public class GroupNode : ConditionNode
{
    public required LogicalOperator Operator { get; init; }
    public required List<ConditionNode> Children { get; init; }
}
