namespace Qbitflow.Core.Domain.Actions;

/// <summary>
/// Resumes a torrent. Idempotency: skipped for a torrent that is not currently
/// stopped/paused (any state other than a "stopped*" / "paused*" one).
/// </summary>
public class StartTorrentAction : ActionDefinition
{
}

/// <summary>
/// Stops (pauses) a torrent. Idempotency: skipped for a torrent already in a
/// "stopped*" / "paused*" state.
/// </summary>
public class StopTorrentAction : ActionDefinition
{
}
