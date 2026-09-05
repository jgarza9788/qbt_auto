namespace Qbitflow.Engine.Scheduling;

public class CronValidationResult
{
    public required bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }

    public static CronValidationResult Ok() => new() { IsValid = true };
    public static CronValidationResult Failure(string message) => new() { IsValid = false, ErrorMessage = message };
}
