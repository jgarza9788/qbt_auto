namespace Qbitflow.Core.Domain.SourceData;

public class ConnectionTestResult
{
    public required bool Success { get; init; }
    public string? Message { get; init; }
    public TimeSpan Duration { get; init; }
}
