namespace Qbitflow.Engine.Conditions;

/// <summary>One queryable relation in the snapshot schema (a table or the play_counts view), and the fields a condition can reference within it.</summary>
public class RelationDefinition
{
    public required string TableName { get; init; }
    public required string AliasPrefix { get; init; }
    public required IReadOnlyDictionary<string, FieldDefinition> Fields { get; init; }
}
