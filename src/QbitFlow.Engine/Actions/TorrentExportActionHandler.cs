using Microsoft.Extensions.Logging;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;

namespace QbitFlow.Engine.Actions;

/// <summary>
/// Saves the torrent's <c>.torrent</c> file into a folder (default <c>EXPORTS_DIR</c> env or
/// <c>./exports</c>). Idempotent: skips if the file already exists.
/// </summary>
public sealed class TorrentExportActionHandler : IActionHandler
{
    public string Type => "torrent.export";

    public ActionParamSchema Schema => new("Export .torrent",
        [new ActionParam("folder", "Target folder", "string", Required: false, "Default: EXPORTS_DIR env or ./exports")]);

    public async Task<ActionOutcome> ApplyAsync(ActionContext ctx)
    {
        if (ctx.Match != true) return ActionOutcome.NotApplicable;

        var folder = ctx.Param("folder").Trim();
        if (folder.Length == 0)
            folder = Environment.GetEnvironmentVariable("EXPORTS_DIR") ?? "exports";

        string path;
        try
        {
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, SafeName(ctx.Torrent.Name) + ".torrent");
        }
        catch (Exception ex)
        {
            ctx.Log.LogWarning("torrent.export: cannot use folder '{Folder}': {Error}", folder, ex.Message);
            return ActionOutcome.Skipped;
        }

        if (File.Exists(path)) return ActionOutcome.Skipped;

        if (ctx.DryRun)
        {
            ctx.Log.LogInformation("[dry-run] would export {Name} → {Path}", ctx.Torrent.Name, path);
            return ActionOutcome.WouldApply;
        }

        try
        {
            await using var src = await ctx.Qbt.ExportTorrentAsync(ctx.Torrent.Hash, ctx.CancellationToken);
            await using var dst = File.Create(path);
            await src.CopyToAsync(dst, ctx.CancellationToken);
            ctx.Log.LogInformation("Exported {Name} → {Path}", ctx.Torrent.Name, path);
            return ActionOutcome.Applied;
        }
        catch (Exception ex)
        {
            ctx.Log.LogWarning("torrent.export failed for {Name}: {Error}", ctx.Torrent.Name, ex.Message);
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
            return ActionOutcome.Error;
        }
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return name.Length == 0 ? "torrent" : name[..Math.Min(name.Length, 180)];
    }
}
