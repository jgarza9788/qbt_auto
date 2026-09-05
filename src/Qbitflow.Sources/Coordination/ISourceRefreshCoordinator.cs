using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Sources.Coordination;

public interface ISourceRefreshCoordinator
{
    /// <summary>
    /// Refreshes every given instance concurrently (bounded by the per-host concurrency
    /// limiter). Instances whose cache entry is still within TTL are skipped unless
    /// forceRefresh is set. One instance failing is isolated -- it's reported in the
    /// returned summary, it never throws out of this call or blocks the others.
    /// </summary>
    Task<SourceRefreshSummary> RefreshAsync(IReadOnlyList<SourceConnectionInfo> instances, bool forceRefresh, CancellationToken ct = default);
}
