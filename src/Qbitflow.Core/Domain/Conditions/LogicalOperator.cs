using System.Text.Json.Serialization;

namespace Qbitflow.Core.Domain.Conditions;

/// <summary>[JsonConverter] is required: System.Text.Json serializes enums as integers by default, and this tree round-trips through JSON text (Rule.ConditionTreeJson) rather than staying in-memory.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogicalOperator
{
    And,
    Or
}
