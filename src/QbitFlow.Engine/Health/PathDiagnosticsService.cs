using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Sources;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Engine.Health;

/// <summary>
/// One-shot check ~20 s after boot: for each qBittorrent target, is each torrent's <c>SavePath</c>
/// visible on this host? If not, <c>torrent.move</c> / <c>script.run</c> / drive fields won't work —
/// almost always a missing bind-mount.
/// </summary>
public sealed class PathDiagnosticsService(
    IServiceScopeFactory scopeFactory,
    SourceAdapterFactory adapters,
    ILogger<PathDiagnosticsService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch { return; }

        List<Guid> qbtIds;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            qbtIds = await db.SourceConnections
                .Where(s => s.Enabled && s.Kind == SourceKind.Qbt)
                .Select(s => s.Id).ToListAsync(stoppingToken);
        }
        catch { return; }

        foreach (var id in qbtIds)
        {
            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                var torrents = await adapters.GetQbtAdapter(id).FetchTorrentsAsync(stoppingToken);
                var paths = torrents.Select(t => t.SavePath).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
                var missing = paths.Where(p => !Directory.Exists(p)).ToList();

                if (missing.Count > 0)
                    log.LogWarning(
                        "Path check: {Missing}/{Total} save-path(s) on qBittorrent {Id} are not visible in-container "
                        + "(bind-mount them at the same path). e.g. {Example}",
                        missing.Count, paths.Count, id, missing.Take(3));
                else if (paths.Count > 0)
                    log.LogInformation("Path check: all {Total} save-path(s) on qBittorrent {Id} are visible.", paths.Count, id);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Path check skipped for qBittorrent {Id}", id);
            }
        }
    }
}
