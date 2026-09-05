using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Sources.Adapters;
using Qbitflow.Sources.Cache;
using Qbitflow.Sources.Concurrency;

namespace Qbitflow.Sources.Coordination;

public class SourceRefreshCoordinator(
    ISourceAdapterResolver adapterResolver,
    ISourceDataCache cache,
    IHostConcurrencyLimiter concurrencyLimiter,
    SourceCacheOptions cacheOptions,
    ILogger<SourceRefreshCoordinator> logger) : ISourceRefreshCoordinator
{
    public async Task<SourceRefreshSummary> RefreshAsync(IReadOnlyList<SourceConnectionInfo> instances, bool forceRefresh, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(instances.Select(instance => RefreshOneAsync(instance, forceRefresh, ct)));
        return new SourceRefreshSummary { Results = results.ToList() };
    }

    private async Task<InstanceRefreshResult> RefreshOneAsync(SourceConnectionInfo instance, bool forceRefresh, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ttl = cacheOptions.GetTtl(instance.SourceType);

        if (!forceRefresh && cache.TryGet(instance.InstanceId, ttl, out _))
        {
            return new InstanceRefreshResult
            {
                InstanceId = instance.InstanceId,
                InstanceName = instance.InstanceName,
                Success = true,
                FromCache = true,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        try
        {
            var adapter = adapterResolver.Resolve(instance.SourceType);
            var host = new Uri(instance.BaseUrl).Host;

            SourceFetchResult result;
            await using (await concurrencyLimiter.AcquireAsync(host, ct))
            {
                result = await adapter.FetchAsync(instance, ct);
            }

            cache.Set(instance.InstanceId, result);

            return new InstanceRefreshResult
            {
                InstanceId = instance.InstanceId,
                InstanceName = instance.InstanceName,
                Success = true,
                FromCache = false,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            // Failure isolation: one dead source must not break the refresh cycle for the rest.
            logger.LogWarning(ex, "Refresh failed for instance {InstanceName} ({SourceType})", instance.InstanceName, instance.SourceType);
            return new InstanceRefreshResult
            {
                InstanceId = instance.InstanceId,
                InstanceName = instance.InstanceName,
                Success = false,
                Error = ex.Message,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
}
