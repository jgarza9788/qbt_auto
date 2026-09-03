using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Sources;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Engine.Health;

/// <summary>Polls every enabled source on an interval and records its health.</summary>
public sealed class SourceHealthService(
    IServiceScopeFactory scopeFactory,
    SourceAdapterFactory adapters,
    ILogger<SourceHealthService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // small initial delay so startup isn't noisy
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await CheckAllAsync(stoppingToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogError(ex, "Source health sweep failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task CheckAllAsync(CancellationToken ct)
    {
        List<(Guid Id, SourceKind Kind)> sources;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            sources = await db.SourceConnections
                .Where(s => s.Enabled)
                .Select(s => new ValueTuple<Guid, SourceKind>(s.Id, s.Kind))
                .ToListAsync(ct);
        }

        foreach (var (id, kind) in sources)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProbeAsync(id, kind, ct);
            await WriteAsync(id, result, ct);
        }
    }

    private async Task<HealthResult> ProbeAsync(Guid id, SourceKind kind, CancellationToken ct)
    {
        try
        {
            return kind == SourceKind.Qbt
                ? await adapters.GetQbtAdapter(id).TestAsync(ct)
                : await adapters.GetMediaAdapter(id).TestAsync(ct);
        }
        catch (Exception ex)
        {
            return HealthResult.Unhealthy(ex.Message);
        }
    }

    private async Task WriteAsync(Guid id, HealthResult result, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.SourceConnections.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (row is null) return;

        row.HealthState = result.Ok ? HealthState.Healthy : HealthState.Unreachable;
        row.LastCheckedUtc = DateTimeOffset.UtcNow;
        row.LatencyMs = result.LatencyMs;
        row.LastError = result.Ok ? null : Truncate(result.Error, 500);
        await db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}
