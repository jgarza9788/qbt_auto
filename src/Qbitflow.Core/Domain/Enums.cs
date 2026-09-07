namespace Qbitflow.Core.Domain;

public enum SourceType
{
    Qbittorrent,
    Plex,
    Jellyfin,
    Tautulli,
    Jellystat,
    Jellyglance
}

public enum ParallelismLevel
{
    Low,
    Medium,
    High,
    VeryHigh
}

public enum RunOutcome
{
    Success,
    PartialFailure,
    Failed,
    Skipped
}
