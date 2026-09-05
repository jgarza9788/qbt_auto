using System.Collections.Concurrent;
using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Sources.Cache;

/// <summary>
/// Per-instance cache of the last successful fetch, with per-source TTL (see
/// SourceCacheOptions) and explicit invalidation. This is what makes "one fetch per
/// source per refresh cycle, shared across all rules" possible -- rules never call
/// adapters directly, they read whatever the last refresh cycle put in here.
/// </summary>
internal record CacheEntry(SourceFetchResult Result, DateTimeOffset FetchedAt);

public class InMemorySourceDataCache : ISourceDataCache
{
    private readonly ConcurrentDictionary<int, CacheEntry> _entries = new();

    public bool TryGet(int instanceId, TimeSpan ttl, out SourceFetchResult? result)
    {
        if (_entries.TryGetValue(instanceId, out var entry) && DateTimeOffset.UtcNow - entry.FetchedAt <= ttl)
        {
            result = entry.Result;
            return true;
        }

        result = null;
        return false;
    }

    public bool TryGetAny(int instanceId, out SourceFetchResult? result)
    {
        if (_entries.TryGetValue(instanceId, out var entry))
        {
            result = entry.Result;
            return true;
        }

        result = null;
        return false;
    }

    public void Set(int instanceId, SourceFetchResult result) =>
        _entries[instanceId] = new CacheEntry(result, DateTimeOffset.UtcNow);

    public void Invalidate(int instanceId) => _entries.TryRemove(instanceId, out _);

    public void InvalidateAll() => _entries.Clear();
}
