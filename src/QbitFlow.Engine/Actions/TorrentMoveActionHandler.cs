using Microsoft.Extensions.Logging;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;

namespace QbitFlow.Engine.Actions;

/// <summary>
/// Moves a torrent's files to another folder (turns automatic torrent management off first).
/// The <c>path</c> parameter may contain <c>&lt;placeholder&gt;</c> tokens (already substituted).
/// </summary>
public sealed class TorrentMoveActionHandler : IActionHandler
{
    public string Type => "torrent.move";

    public ActionParamSchema Schema => new("Move torrent",
        [new ActionParam("path", "Target folder", "string", Required: true, "Supports <placeholder> tokens, e.g. /media/H00/<Category>.")]);

    public async Task<ActionOutcome> ApplyAsync(ActionContext ctx)
    {
        if (ctx.Match != true) return ActionOutcome.NotApplicable;

        var path = ctx.Param("path").Trim();
        if (path.Length == 0) return ActionOutcome.NotApplicable;

        if (!Directory.Exists(path))
        {
            ctx.Log.LogWarning("Move target does not exist, skipping {Name} → {Path}", ctx.Torrent.Name, path);
            return ActionOutcome.Skipped;
        }

        if (string.Equals(ctx.Torrent.SavePath.TrimEnd('/', '\\'), path.TrimEnd('/', '\\'), StringComparison.Ordinal))
            return ActionOutcome.Skipped;

        if (ctx.DryRun)
        {
            ctx.Log.LogInformation("[dry-run] would move {Name} → {Path}", ctx.Torrent.Name, path);
            return ActionOutcome.WouldApply;
        }

        await ctx.Qbt.SetLocationAsync(ctx.Torrent.Hash, path, disableAutoManagement: true, ctx.CancellationToken);
        ctx.Log.LogInformation("Moved {Name} → {Path}", ctx.Torrent.Name, path);
        return ActionOutcome.Applied;
    }
}
