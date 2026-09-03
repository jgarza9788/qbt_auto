using Microsoft.Extensions.Logging;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;

namespace QbitFlow.Engine.Actions;

/// <summary>Sets a category (and turns on automatic torrent management so qBittorrent relocates files).</summary>
public sealed class CategorySetActionHandler : IActionHandler
{
    public string Type => "category.set";

    public ActionParamSchema Schema => new("Set category",
        [new ActionParam("category", "Category", "string", Required: true)]);

    public async Task<ActionOutcome> ApplyAsync(ActionContext ctx)
    {
        if (ctx.Match != true) return ActionOutcome.NotApplicable;

        var category = ctx.Param("category").Trim();
        if (category.Length == 0) return ActionOutcome.NotApplicable;
        if (string.Equals(ctx.Torrent.Category, category, StringComparison.Ordinal))
            return ActionOutcome.Skipped;

        if (ctx.DryRun)
        {
            ctx.Log.LogInformation("[dry-run] would set category of {Name} to {Category}", ctx.Torrent.Name, category);
            return ActionOutcome.WouldApply;
        }

        await ctx.Qbt.SetCategoryAsync(ctx.Torrent.Hash, category, enableAutoManagement: true, ctx.CancellationToken);
        ctx.Log.LogInformation("Set category of {Name} to {Category}", ctx.Torrent.Name, category);
        return ActionOutcome.Applied;
    }
}
