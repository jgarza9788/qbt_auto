using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Pipelines;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Engine.Scheduling;

/// <summary>
/// The internal cron timer. Ticks every ~30s, runs any due pipeline directly (up to 2 at once), and
/// enforces one run per pipeline with a per-pipeline semaphore. No external cron required.
/// </summary>
public sealed class SchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<SchedulerService> log) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly SemaphoreSlim _globalConcurrency = new(2, 2);
    private CancellationToken _hostStopping = CancellationToken.None;

    /// <summary>Cancels the in-flight run for a pipeline, if any. Returns false if nothing is running.</summary>
    public bool CancelRun(Guid pipelineId) =>
        _running.TryGetValue(pipelineId, out var cts) && Try(() => cts.Cancel());

    private static bool Try(Action a) { try { a(); return true; } catch { return false; } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _hostStopping = stoppingToken;
        await ClearStaleRunningFlagsAsync(stoppingToken);

        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Scheduler tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        List<Guid> due;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            due = await db.Pipelines
                .Where(p => p.Enabled && !p.IsRunning && (p.NextRunUtc == null || p.NextRunUtc <= now))
                .Select(p => p.Id)
                .ToListAsync(ct);
        }

        foreach (var pipelineId in due)
        {
            var gate = _locks.GetOrAdd(pipelineId, _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(0))
            {
                log.LogDebug("Pipeline {PipelineId} still running — skipped this tick", pipelineId);
                continue;
            }

            _ = RunGuardedAsync(pipelineId, RunTrigger.Schedule, dryRun: null, gate);
        }
    }

    /// <summary>Run-now: fire a pipeline immediately, respecting the per-pipeline lock. The run's
    /// lifetime is tied to host shutdown, never to the calling request.</summary>
    public Task<bool> TriggerNowAsync(Guid pipelineId, bool? dryRun, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(pipelineId, static _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
            return Task.FromResult(false);

        _ = RunGuardedAsync(pipelineId, RunTrigger.Manual, dryRun, gate);
        return Task.FromResult(true);
    }

    private async Task RunGuardedAsync(Guid pipelineId, RunTrigger trigger, bool? dryRun, SemaphoreSlim gate)
    {
        // The run's lifetime is tied to host shutdown, never to any HTTP request.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_hostStopping);
        _running[pipelineId] = cts;

        await _globalConcurrency.WaitAsync(cts.Token);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<IPipelineRunner>();
            await runner.RunAsync(pipelineId, trigger, dryRun, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.LogError(ex, "Pipeline {PipelineId} run failed", pipelineId);
        }
        finally
        {
            _running.TryRemove(pipelineId, out _);
            cts.Dispose();
            _globalConcurrency.Release();
            gate.Release();
        }
    }

    private async Task ClearStaleRunningFlagsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stale = await db.Pipelines.Where(p => p.IsRunning).ToListAsync(ct);
            foreach (var p in stale) p.IsRunning = false;
            if (stale.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                log.LogInformation("Cleared {Count} stale IsRunning flag(s) on startup", stale.Count);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not clear stale IsRunning flags");
        }
    }
}
