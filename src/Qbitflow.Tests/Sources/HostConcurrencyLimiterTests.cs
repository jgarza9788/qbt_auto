using Microsoft.Extensions.DependencyInjection;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Interfaces;
using Qbitflow.Sources.Concurrency;
using Xunit;

namespace Qbitflow.Tests.Sources;

public class HostConcurrencyLimiterTests
{
    private sealed class FixedLevelProvider(ParallelismLevel level) : IParallelismSettingsProvider
    {
        public Task<ParallelismLevel> GetLevelAsync(CancellationToken ct = default) => Task.FromResult(level);
    }

    [Fact]
    public async Task AcquireAsync_LimitsConcurrencyToConfiguredWorkerCount()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IParallelismSettingsProvider>(new FixedLevelProvider(ParallelismLevel.Low)); // Low = 2 workers
        await using var provider = services.BuildServiceProvider();

        var limiter = new HostConcurrencyLimiter(provider.GetRequiredService<IServiceScopeFactory>());

        var concurrentCount = 0;
        var maxObserved = 0;
        var gate = new object();

        async Task WorkAsync()
        {
            await using var _ = await limiter.AcquireAsync("example.com");
            lock (gate)
            {
                concurrentCount++;
                maxObserved = Math.Max(maxObserved, concurrentCount);
            }
            await Task.Delay(150);
            lock (gate)
            {
                concurrentCount--;
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => WorkAsync()));

        Assert.Equal(2, maxObserved);
    }

    [Fact]
    public async Task AcquireAsync_DoesNotThrottleAcrossDifferentHosts()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IParallelismSettingsProvider>(new FixedLevelProvider(ParallelismLevel.Low)); // Low = 2 workers per host
        await using var provider = services.BuildServiceProvider();

        var limiter = new HostConcurrencyLimiter(provider.GetRequiredService<IServiceScopeFactory>());

        var concurrentCount = 0;
        var maxObserved = 0;
        var gate = new object();

        async Task WorkAsync(string host)
        {
            await using var _ = await limiter.AcquireAsync(host);
            lock (gate)
            {
                concurrentCount++;
                maxObserved = Math.Max(maxObserved, concurrentCount);
            }
            await Task.Delay(150);
            lock (gate)
            {
                concurrentCount--;
            }
        }

        // 3 different hosts, 2 requests each -- each host is capped at 2, but the hosts
        // don't share a limiter, so more than 2 requests should run at once overall.
        var tasks = new List<Task>();
        foreach (var host in new[] { "a.local", "b.local", "c.local" })
        {
            tasks.Add(WorkAsync(host));
            tasks.Add(WorkAsync(host));
        }
        await Task.WhenAll(tasks);

        Assert.True(maxObserved > 2, $"expected more than 2 concurrent across hosts, observed {maxObserved}");
    }
}
