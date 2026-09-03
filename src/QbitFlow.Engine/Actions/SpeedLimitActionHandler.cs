using System.Globalization;
using Microsoft.Extensions.Logging;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;

namespace QbitFlow.Engine.Actions;

/// <summary>
/// Sets per-torrent upload / download limits. Values are KiB/s: <c>0</c> = unlimited, <c>-1</c> = leave
/// that direction alone. If both are <c>0</c> the torrent is paused (legacy <c>AutoSpeed</c> behaviour).
/// </summary>
public sealed class SpeedLimitActionHandler : IActionHandler
{
    public string Type => "speed.limit";

    public ActionParamSchema Schema => new("Limit speed",
    [
        new ActionParam("uploadKb", "Upload KiB/s", "number", Required: true, "0 = unlimited, -1 = leave alone"),
        new ActionParam("downloadKb", "Download KiB/s", "number", Required: true, "0 = unlimited, -1 = leave alone"),
    ]);

    public async Task<ActionOutcome> ApplyAsync(ActionContext ctx)
    {
        if (ctx.Match != true) return ActionOutcome.NotApplicable;

        var upKb = ParseKb(ctx.Param("uploadKb", "-1"));
        var downKb = ParseKb(ctx.Param("downloadKb", "-1"));

        if (upKb == 0 && downKb == 0)
        {
            if (ctx.DryRun) { ctx.Log.LogInformation("[dry-run] would pause {Name} (both limits 0)", ctx.Torrent.Name); return ActionOutcome.WouldApply; }
            await ctx.Qbt.PauseAsync(ctx.Torrent.Hash, ctx.CancellationToken);
            ctx.Log.LogInformation("Paused {Name} (both limits 0)", ctx.Torrent.Name);
            return ActionOutcome.Applied;
        }

        var acted = false;

        if (upKb >= 0)
        {
            var bytes = upKb * 1024L;
            if (ctx.Torrent.UploadLimit != bytes)
            {
                if (ctx.DryRun) { ctx.Log.LogInformation("[dry-run] would set UL limit of {Name} to {Kb} KiB/s", ctx.Torrent.Name, upKb); acted = true; }
                else { await ctx.Qbt.SetUploadLimitAsync(ctx.Torrent.Hash, bytes, ctx.CancellationToken); ctx.Log.LogInformation("Set UL limit of {Name} to {Kb} KiB/s", ctx.Torrent.Name, upKb); acted = true; }
            }
        }

        if (downKb >= 0)
        {
            var bytes = downKb * 1024L;
            if (ctx.Torrent.DownloadLimit != bytes)
            {
                if (ctx.DryRun) { ctx.Log.LogInformation("[dry-run] would set DL limit of {Name} to {Kb} KiB/s", ctx.Torrent.Name, downKb); acted = true; }
                else { await ctx.Qbt.SetDownloadLimitAsync(ctx.Torrent.Hash, bytes, ctx.CancellationToken); ctx.Log.LogInformation("Set DL limit of {Name} to {Kb} KiB/s", ctx.Torrent.Name, downKb); acted = true; }
            }
        }

        return acted
            ? (ctx.DryRun ? ActionOutcome.WouldApply : ActionOutcome.Applied)
            : ActionOutcome.Skipped;
    }

    private static long ParseKb(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? (long)d : -1);
}
