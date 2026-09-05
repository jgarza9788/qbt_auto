namespace Qbitflow.Engine.Conditions;

public class CompiledQuery
{
    public required string Sql { get; init; }
    public required IReadOnlyDictionary<string, object> Parameters { get; init; }
}
