using System.Text.Json.Serialization;

namespace Qbitflow.Core.Domain.Conditions;

/// <summary>
/// A structured condition tree node. This -- not a raw SQL string, not an eval'd
/// expression -- is what Rule.ConditionTreeJson deserializes into. Qbitflow.Engine's
/// ConditionSqlCompiler is the only thing that turns this into SQL, and it only ever
/// emits parameterized text; there is no path from user input to string-concatenated SQL.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(GroupNode), "group")]
[JsonDerivedType(typeof(ComparisonNode), "comparison")]
[JsonDerivedType(typeof(NotNode), "not")]
[JsonDerivedType(typeof(ExistsNode), "exists")]
public abstract class ConditionNode
{
}
