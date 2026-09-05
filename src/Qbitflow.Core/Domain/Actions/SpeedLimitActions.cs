namespace Qbitflow.Core.Domain.Actions;

/// <summary>Bytes/sec; 0 means unlimited. Idempotency: skipped for a torrent whose upload limit already equals LimitBytesPerSec.</summary>
public class SetUploadLimitAction : ActionDefinition
{
    public required long LimitBytesPerSec { get; init; }
}

/// <summary>Bytes/sec; 0 means unlimited. Idempotency: skipped for a torrent whose download limit already equals LimitBytesPerSec.</summary>
public class SetDownloadLimitAction : ActionDefinition
{
    public required long LimitBytesPerSec { get; init; }
}
