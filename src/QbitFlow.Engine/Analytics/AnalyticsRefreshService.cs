using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace QbitFlow.Engine.Analytics;

/// <summary>
/// Runs <see cref="AnalyticsService.RefreshAsync"/> on its own (longer) interval — the only schedule
/// that touches Plex / Jellyfin. Single-flighted; also exposes an on-demand trigger.
/// </summary>
public sealed class AnalyticsRefreshService(
    IServiceScopeFactory scopeFactory,
    ILogger<AnalyticsRefreshService> log) : BackgroundService
{
    private const int DefaultIntervalMinutes = 360;
    private const int FloorMinutes = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await TriggerAsync(stoppingToken);

            var minutes = Math.Max(FloorMinutes, await ReadIntervalMinutesAsync(stoppingToken));
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Runs a refresh now unless one is already running (in which case it returns immediately).</summary>
    public async Task<bool> TriggerAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            log.LogDebug("Analytics refresh already running — trigger ignored");
            return false;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();
            await svc.RefreshAsync(ct);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            log.LogError(ex, "Analytics refresh failed");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ReadIntervalMinutesAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<Infrastructure.Config.AppSettingStore>();
            return await settings.GetIntAsync(
                Core.Domain.AppSetting.AnalyticsIntervalMinutes, DefaultIntervalMinutes, ct);
        }
        catch
        {
            return DefaultIntervalMinutes;
        }
    }
}
