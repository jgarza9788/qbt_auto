using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Sources.Cache;

public interface ISourceDataCache
{
    bool TryGet(int instanceId, TimeSpan ttl, out SourceFetchResult? result);

    /// <summary>Ignores TTL entirely -- for a caller (RuleRunner) that just triggered a refresh and wants whatever that refresh cycle left behind, not a second freshness judgment.</summary>
    bool TryGetAny(int instanceId, out SourceFetchResult? result);

    void Set(int instanceId, SourceFetchResult result);
    void Invalidate(int instanceId);
    void InvalidateAll();
}
