using Microsoft.Extensions.DependencyInjection;
using QbitFlow.Infrastructure.Config;

namespace QbitFlow.Web.Startup;

internal static class ConfigImport
{
    /// <summary>
    /// On first boot, if <c>CONFIG_IMPORT_PATH</c> points at a readable legacy <c>config.json</c> and
    /// no pipelines exist yet, import it.
    /// </summary>
    public static async Task RunFirstBootAsync(IServiceProvider services, IConfiguration config, CancellationToken ct)
    {
        var path = config["CONFIG_IMPORT_PATH"] ?? Environment.GetEnvironmentVariable("CONFIG_IMPORT_PATH");
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("ConfigImport");

        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path))
        {
            log.LogInformation("CONFIG_IMPORT_PATH={Path} not found — skipping first-boot import", path);
            return;
        }

        try
        {
            var json5 = await File.ReadAllTextAsync(path, ct);
            var importer = services.GetRequiredService<ConfigImportService>();
            var result = await importer.ImportAsync(json5, ImportMode.FirstBootOnly, ct);
            log.LogInformation("First-boot import: {Reason} (sources={Sources} rules={Rules})",
                result.Reason, result.Sources, result.Rules);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "First-boot config import failed");
        }
    }
}
