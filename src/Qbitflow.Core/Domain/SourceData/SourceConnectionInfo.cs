namespace Qbitflow.Core.Domain.SourceData;

/// <summary>
/// Everything an adapter needs to talk to one instance, with credentials already
/// decrypted. Built by whoever loads Instance rows from the database (Infrastructure
/// owns ISecretProtector; Sources adapters never see the encrypted form) -- this keeps
/// Qbitflow.Sources free of a dependency on Qbitflow.Infrastructure.
/// </summary>
public class SourceConnectionInfo
{
    public required int InstanceId { get; init; }
    public required string InstanceName { get; init; }
    public required SourceType SourceType { get; init; }
    public required string BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public bool VerifySsl { get; init; } = true;
    public string? ExtraConfigJson { get; init; }
}
