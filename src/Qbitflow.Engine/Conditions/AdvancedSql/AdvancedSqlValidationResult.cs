namespace Qbitflow.Engine.Conditions.AdvancedSql;

public class AdvancedSqlValidationResult
{
    public required bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>The actual executable SELECT (only set when IsValid).</summary>
    public string? CompiledSql { get; init; }

    public static AdvancedSqlValidationResult Ok(string compiledSql) => new() { IsValid = true, CompiledSql = compiledSql };
    public static AdvancedSqlValidationResult Failure(string message) => new() { IsValid = false, ErrorMessage = message };
}
