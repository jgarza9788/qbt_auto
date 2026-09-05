using Microsoft.Extensions.Logging.Abstractions;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Core.Interfaces;
using Qbitflow.Sources.Adapters;
using Qbitflow.Sources.Cache;
using Qbitflow.Sources.Concurrency;
using Qbitflow.Sources.Coordination;
using Xunit;

namespace Qbitflow.Tests.Sources;

public class SourceRefreshCoordinatorTests
{
    private sealed class StubAdapter(SourceType type, Func<SourceConnectionInfo, Task<SourceFetchResult>> fetch) : ISourceAdapter
    {
        public SourceType SourceType => type;

        public Task<ConnectionTestResult> TestConnectionAsync(SourceConnectionInfo connection, CancellationToken ct = default)
            => Task.FromResult(new ConnectionTestResult { Success = true });

        public Task<SourceFetchResult> FetchAsync(SourceConnectionInfo connection, CancellationToken ct = default)
            => fetch(connection);
    }

    private sealed class NoOpConcurrencyLimiter : IHostConcurrencyLimiter
    {
        public Task<IAsyncDisposable> AcquireAsync(string host, CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable>(new NoOpDisposable());

        private sealed class NoOpDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task RefreshAsync_IsolatesFailures_OneDeadInstanceDoesNotAffectOthers()
    {
        var healthyAdapter = new StubAdapter(SourceType.Qbittorrent, _ => Task.FromResult(new SourceFetchResult
        {
            Torrents = [new TorrentRecord { InstanceId = 1, InstanceName = "Healthy", Hash = "h1", Name = "T1" }]
        }));
        var deadAdapter = new StubAdapter(SourceType.Plex, _ => throw new HttpRequestException("connection refused"));

        var resolver = new SourceAdapterResolver([healthyAdapter, deadAdapter]);
        var cache = new InMemorySourceDataCache();
        var coordinator = new SourceRefreshCoordinator(
            resolver, cache, new NoOpConcurrencyLimiter(), new SourceCacheOptions(), NullLogger<SourceRefreshCoordinator>.Instance);

        var instances = new List<SourceConnectionInfo>
        {
            new() { InstanceId = 1, InstanceName = "Healthy", SourceType = SourceType.Qbittorrent, BaseUrl = "http://qbt.local" },
            new() { InstanceId = 2, InstanceName = "Dead", SourceType = SourceType.Plex, BaseUrl = "http://plex.local" }
        };

        var summary = await coordinator.RefreshAsync(instances, forceRefresh: true);

        Assert.Equal(2, summary.Results.Count);
        var healthyResult = summary.Results.Single(r => r.InstanceId == 1);
        var deadResult = summary.Results.Single(r => r.InstanceId == 2);

        Assert.True(healthyResult.Success);
        Assert.False(deadResult.Success);
        Assert.Contains("connection refused", deadResult.Error);

        Assert.True(cache.TryGet(1, TimeSpan.FromMinutes(5), out var cached));
        Assert.Single(cached!.Torrents);
        Assert.False(cache.TryGet(2, TimeSpan.FromMinutes(5), out _));
    }

    [Fact]
    public async Task RefreshAsync_SkipsFreshCache_UnlessForced()
    {
        var callCount = 0;
        var adapter = new StubAdapter(SourceType.Qbittorrent, _ =>
        {
            callCount++;
            return Task.FromResult(new SourceFetchResult());
        });

        var resolver = new SourceAdapterResolver([adapter]);
        var cache = new InMemorySourceDataCache();
        var coordinator = new SourceRefreshCoordinator(
            resolver, cache, new NoOpConcurrencyLimiter(), new SourceCacheOptions(), NullLogger<SourceRefreshCoordinator>.Instance);

        var instance = new SourceConnectionInfo { InstanceId = 1, InstanceName = "A", SourceType = SourceType.Qbittorrent, BaseUrl = "http://qbt.local" };

        await coordinator.RefreshAsync([instance], forceRefresh: false);
        await coordinator.RefreshAsync([instance], forceRefresh: false);
        Assert.Equal(1, callCount);

        await coordinator.RefreshAsync([instance], forceRefresh: true);
        Assert.Equal(2, callCount);
    }
}
