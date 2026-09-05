namespace Qbitflow.Engine.Conditions;

/// <summary>
/// One documented field a condition can reference. SqlExpression uses "{alias}" as a
/// placeholder for whatever table alias the compiler assigns at compile time (the same
/// field definition is reused whether it's compiled for the outer torrents row or an
/// EXISTS subquery's row).
/// </summary>
public class FieldDefinition
{
    public required string Key { get; init; }
    public required string SqlExpression { get; init; }
    public required FieldValueType ValueType { get; init; }
    public string? Description { get; init; }
    public string? ExampleValue { get; init; }

    public string Resolve(string alias) => SqlExpression.Replace("{alias}", alias);
}
