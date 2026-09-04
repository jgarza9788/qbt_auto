using FluentAssertions;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;
using QbitFlow.Engine.Sources;

namespace QbitFlow.Tests.Sources;

public class TorrentSnapshotCacheTests
{
    private static readonly Guid InstanceA = Guid.NewGuid();
    private static readonly Guid InstanceB = Guid.NewGuid();

    [Fact]
    public async Task Repeated_reads_inside_the_window_hit_qBittorrent_once()
    {
        var gateways = new CountingGatewayFactory();
        var cache = new TorrentSnapshotCache(gateways);

        for (var i = 0; i < 5; i++)
            await cache.GetAsync(InstanceA, TimeSpan.FromMinutes(5), default);

        gateways.FetchCount.Should().Be(1);
    }

    [Fact]
    public async Task A_zero_max_age_always_refetches()
    {
        var gateways = new CountingGatewayFactory();
        var cache = new TorrentSnapshotCache(gateways);

        await cache.GetAsync(InstanceA, TimeSpan.Zero, default);
        await cache.GetAsync(InstanceA, TimeSpan.Zero, default);

        gateways.FetchCount.Should().Be(2);
    }

    [Fact]
    public async Task Invalidate_forces_the_next_read_to_refetch()
    {
        var gateways = new CountingGatewayFactory();
        var cache = new TorrentSnapshotCache(gateways);

        await cache.GetAsync(InstanceA, TimeSpan.FromMinutes(5), default);
        cache.Invalidate(InstanceA);
        await cache.GetAsync(InstanceA, TimeSpan.FromMinutes(5), default);

        gateways.FetchCount.Should().Be(2);
    }

    [Fact]
    public async Task Instances_are_cached_independently()
    {
        var gateways = new CountingGatewayFactory();
        var cache = new TorrentSnapshotCache(gateways);

        await cache.GetAsync(InstanceA, TimeSpan.FromMinutes(5), default);
        await cache.GetAsync(InstanceB, TimeSpan.FromMinutes(5), default);
        await cache.GetAsync(InstanceA, TimeSpan.FromMinutes(5), default);

        gateways.FetchCount.Should().Be(2);
        cache.Invalidate(InstanceA);
        await cache.GetAsync(InstanceB, TimeSpan.FromMinutes(5), default);
        gateways.FetchCount.Should().Be(2);   // B untouched by A's invalidation
    }

    [Fact]
    public async Task Concurrent_misses_for_one_instance_share_a_single_fetch()
    {
        var gateways = new CountingGatewayFactory { FetchDelay = TimeSpan.FromMilliseconds(50) };
        var cache = new TorrentSnapshotCache(gateways);

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => cache.GetAsync(InstanceA, TimeSpan.FromMinutes(5), default)));

        gateways.FetchCount.Should().Be(1);
    }

    private sealed class CountingGatewayFactory : IQbtGatewayFactory, IQbtAdapter
    {
        private int _fetches;

        public int FetchCount => Volatile.Read(ref _fetches);
        public TimeSpan FetchDelay { get; init; } = TimeSpan.Zero;

        public Guid SourceId => Guid.Empty;
        public IQbtAdapter GetAdapter(Guid id) => this;
        public IQbtActionTarget GetActionTarget(Guid id) => throw new NotSupportedException();
        public Task<HealthResult> TestAsync(CancellationToken ct) => Task.FromResult(HealthResult.Healthy(1));

        public async Task<IReadOnlyList<TorrentView>> FetchTorrentsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _fetches);
            if (FetchDelay > TimeSpan.Zero) await Task.Delay(FetchDelay, ct);
            return [new TorrentView { Hash = "h1", Name = "One", Category = "x", Tags = [] }];
        }
    }
}
