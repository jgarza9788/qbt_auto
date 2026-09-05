namespace Qbitflow.Core.Domain.Actions;

/// <summary>
/// Idempotency: skipped for a torrent whose save path already equals DestinationPath.
/// If WaitForCompletion, the executor polls until qBittorrent reports the move done
/// (it moves files in the background) before considering the action Applied.
/// </summary>
public class MoveAction : ActionDefinition
{
    public required string DestinationPath { get; init; }
    public bool WaitForCompletion { get; init; } = true;
}
