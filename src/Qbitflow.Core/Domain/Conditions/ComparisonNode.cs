using System.Text.Json;

namespace Qbitflow.Core.Domain.Conditions;

/// <summary>
/// A leaf comparison against a documented field key (see the Phase 7 field reference
/// panel / Qbitflow.Engine's SnapshotFieldRegistry) -- never a raw column/table name the
/// author could point at anything. Value is a scalar for most operators, a JSON array
/// for In/NotIn, and unused for IsNull/IsNotNull.
/// </summary>
public class ComparisonNode : ConditionNode
{
    public required string Field { get; init; }
    public required ComparisonOperator Operator { get; init; }
    public JsonElement? Value { get; init; }
}
