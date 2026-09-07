using Qbitflow.Core.Domain;

namespace Qbitflow.Sources.Cache;

public class SourceCacheOptions
{
    public Dictionary<SourceType, TimeSpan> TtlBySourceType { get; init; } = new()
    {
        [SourceType.Qbittorrent] = TimeSpan.FromMinutes(1),
        [SourceType.Plex] = TimeSpan.FromMinutes(5),
        [SourceType.Jellyfin] = TimeSpan.FromMinutes(5),
        [SourceType.Tautulli] = TimeSpan.FromMinutes(5),
        [SourceType.Jellystat] = TimeSpan.FromMinutes(5),
        [SourceType.Jellyglance] = TimeSpan.FromMinutes(5)
    };

    public TimeSpan Default { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan GetTtl(SourceType type) => TtlBySourceType.GetValueOrDefault(type, Default);
}
