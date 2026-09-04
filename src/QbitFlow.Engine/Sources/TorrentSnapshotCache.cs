using System.Collections.Concurrent;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;

namespace QbitFlow.Engine.Sources;

/// <summary>
/// One shared torrent snapshot per qBittorrent instance. This is what makes a flat rule list cheap:
/// twenty rules over the same instance cost one WebUI fetch, not twenty, and the analytics refresh
/// reuses the same snapshot instead of pulling its own.
/// <para>
/// Freshness is the caller's choice — the rule engine passes its interval
/// (<see cref="Core.Domain.AppSetting.QbtFreshnessSeconds"/>) so a pass never evaluates data older
/// than one cycle, while the slower analytics job accepts a looser age. Concurrent callers for the
/// same instance share a single in-flight fetch rather than stampeding the instance.
/// </para>
/// <para>
/// Registered as a singleton. Invalidate through <see cref="SourceCacheInvalidator"/> whenever a
/// source's connection details change, so a stale snapshot can't outlive the connection it came from.
/// </para>
/// </summary>
public sealed class TorrentSnapshotCache(IQbtGatewayFactory gateways)
{
    private sealed record Entry(DateTimeOffset FetchedUtc, IReadOnlyList<TorrentView> Torrents);

    private readonly ConcurrentDictionary<Guid, Entry> _byInstance = new();

    /// <summary>Per-instance fetch lock, so a miss triggers one request rather than one per caller.</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _fetchLocks = new();

    /// <summary>
    /// Returns the instance's torrents, refetching only when the cached snapshot is older than
    /// <paramref name="maxAge"/>. Fetch failures propagate — callers decide whether an unreachable
    /// instance is fatal or just skipped.
    /// </summary>
    public async Task<IReadOnlyList<TorrentView>> GetAsync(Guid qbtInstanceId, TimeSpan maxAge, CancellationToken ct)
    {
        if (TryGetFresh(qbtInstanceId, maxAge, out var cached))
            return cached;

        var gate = _fetchLocks.GetOrAdd(qbtInstanceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have refreshed it while we waited for the lock.
            if (TryGetFresh(qbtInstanceId, maxAge, out cached))
                return cached;

            var torrents = await gateways.GetAdapter(qbtInstanceId).FetchTorrentsAsync(ct);
            _byInstance[qbtInstanceId] = new Entry(DateTimeOffset.UtcNow, torrents);
            return torrents;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Drops the cached snapshot for one instance, forcing the next <see cref="GetAsync"/> to refetch.
    /// The fetch lock is deliberately kept — it is one small object per instance and discarding it
    /// could let a concurrent fetch escape the lock.
    /// </summary>
    public void Invalidate(Guid qbtInstanceId) => _byInstance.TryRemove(qbtInstanceId, out _);

    private bool TryGetFresh(Guid id, TimeSpan maxAge, out IReadOnlyList<TorrentView> torrents)
    {
        if (_byInstance.TryGetValue(id, out var e) && DateTimeOffset.UtcNow - e.FetchedUtc < maxAge)
        {
            torrents = e.Torrents;
            return true;
        }
        torrents = [];
        return false;
    }
}
