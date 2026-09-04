using QbitFlow.Core.Abstractions;

namespace QbitFlow.Engine.Sources;

/// <summary>
/// Drops every cached artefact for one source connection in the right order, so callers that edit or
/// delete a source can't forget one of them.
/// <para>
/// Two independent caches key off a source id: <see cref="ISourceAdapterFactory"/> holds the built
/// adapter (base URL, credentials, login cookie) and <see cref="TorrentSnapshotCache"/> holds its last
/// torrent list. Clearing only the adapter — as the code did before this existed — left the engine
/// evaluating rules against torrents fetched from the *old* connection for up to one interval.
/// </para>
/// </summary>
public sealed class SourceCacheInvalidator(ISourceAdapterFactory adapters, TorrentSnapshotCache snapshots)
{
    /// <summary>Forgets the adapter and the torrent snapshot for a source. Safe to call for any kind.</summary>
    public void Invalidate(Guid sourceConnectionId)
    {
        adapters.Invalidate(sourceConnectionId);
        snapshots.Invalidate(sourceConnectionId);
    }
}
