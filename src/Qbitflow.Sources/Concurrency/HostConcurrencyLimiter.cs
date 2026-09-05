using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Qbitflow.Core.Interfaces;

namespace Qbitflow.Sources.Concurrency;

public interface IHostConcurrencyLimiter
{
    /// <summary>Blocks until a slot for this host is free; dispose the result to release it.</summary>
    Task<IAsyncDisposable> AcquireAsync(string host, CancellationToken ct = default);
}

/// <summary>
/// One SemaphoreSlim per host, sized from the global ParallelismLevel setting the first
/// time that host is seen. If the level changes at runtime, already-created semaphores
/// keep their original size until the app restarts -- resizing a semaphore with permits
/// already checked out isn't safe to do on the fly, so this is a deliberate simplification.
///
/// IParallelismSettingsProvider is scoped (it reads AppSettings via the DbContext), but
/// this limiter is a singleton, so it resolves that dependency through a short-lived
/// scope on demand rather than taking a captive constructor dependency on it.
/// </summary>
public class HostConcurrencyLimiter(IServiceScopeFactory scopeFactory) : IHostConcurrencyLimiter
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

    public async Task<IAsyncDisposable> AcquireAsync(string host, CancellationToken ct = default)
    {
        if (!_semaphores.TryGetValue(host, out var semaphore))
        {
            using var scope = scopeFactory.CreateScope();
            var settingsProvider = scope.ServiceProvider.GetRequiredService<IParallelismSettingsProvider>();
            var level = await settingsProvider.GetLevelAsync(ct);
            var count = ParallelismMapping.WorkerCount(level);
            semaphore = _semaphores.GetOrAdd(host, _ => new SemaphoreSlim(count, count));
        }

        await semaphore.WaitAsync(ct);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
