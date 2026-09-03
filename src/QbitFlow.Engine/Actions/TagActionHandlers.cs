using Microsoft.Extensions.Logging;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;

namespace QbitFlow.Engine.Actions;

/// <summary>
/// Bidirectional tag: add when criteria are true and the tag is missing; remove when criteria are
/// false and the tag is present. This is the legacy <c>AutoTag</c> behaviour.
/// </summary>
public sealed class TagSyncActionHandler : IActionHandler
{
    public string Type => "tag.sync";

    public ActionParamSchema Schema => new("Sync tag",
        [new ActionParam("tag", "Tag", "string", Required: true, "Added on match, removed when the rule no longer matches.")]);

    public async Task<ActionOutcome> ApplyAsync(ActionContext ctx)
    {
        var tag = ctx.Param("tag").Trim();
        if (tag.Length == 0) return ActionOutcome.NotApplicable;

        var has = ctx.Torrent.Tags.Contains(tag, StringComparer.Ordinal);

        switch (ctx.Match)
        {
            case true when !has:
                if (ctx.DryRun) { LogWould(ctx, "add tag {Tag}", tag); return ActionOutcome.WouldApply; }
                await ctx.Qbt.AddTagAsync(ctx.Torrent.Hash, tag, ctx.CancellationToken);
                ctx.Log.LogInformation("Added tag {Tag} to {Name}", tag, ctx.Torrent.Name);
                return ActionOutcome.Applied;

            case false when has:
                if (ctx.DryRun) { LogWould(ctx, "remove tag {Tag}", tag); return ActionOutcome.WouldApply; }
                await ctx.Qbt.RemoveTagAsync(ctx.Torrent.Hash, tag, ctx.CancellationToken);
                ctx.Log.LogInformation("Removed tag {Tag} from {Name}", tag, ctx.Torrent.Name);
                return ActionOutcome.Applied;

            case null:
                return ActionOutcome.NotApplicable;

            default:
                return ActionOutcome.Skipped; // already in the desired state
        }
    }

    private static void LogWould(ActionContext ctx, string what, string tag) =>
        ctx.Log.LogInformation("[dry-run] would " + what + " on {Name}", tag, ctx.Torrent.Name);
}

/// <summary>One-directional: add a tag on match.</summary>
public sealed class TagAddActionHandler : IActionHandler
{
    public string Type => "tag.add";
    public ActionParamSchema Schema => new("Add tag", [new ActionParam("tag", "Tag", "string", true)]);

    public async Task<ActionOutcome> ApplyAsync(ActionContext ctx)
    {
        if (ctx.Match != true) return ActionOutcome.NotApplicable;
        var tag = ctx.Param("tag").Trim();
        if (tag.Length == 0 || ctx.Torrent.Tags.Contains(tag, StringComparer.Ordinal))
            return ActionOutcome.Skipped;

        if (ctx.DryRun) { ctx.Log.LogInformation("[dry-run] would add tag {Tag} to {Name}", tag, ctx.Torrent.Name); return ActionOutcome.WouldApply; }
        await ctx.Qbt.AddTagAsync(ctx.Torrent.Hash, tag, ctx.CancellationToken);
        ctx.Log.LogInformation("Added tag {Tag} to {Name}", tag, ctx.Torrent.Name);
        return ActionOutcome.Applied;
    }
}

/// <summary>One-directional: remove a tag on match.</summary>
public sealed class TagRemoveActionHandler : IActionHandler
{
    public string Type => "tag.remove";
    public ActionParamSchema Schema => new("Remove tag", [new ActionParam("tag", "Tag", "string", true)]);

    public async Task<ActionOutcome> ApplyAsync(ActionContext ctx)
    {
        if (ctx.Match != true) return ActionOutcome.NotApplicable;
        var tag = ctx.Param("tag").Trim();
        if (tag.Length == 0 || !ctx.Torrent.Tags.Contains(tag, StringComparer.Ordinal))
            return ActionOutcome.Skipped;

        if (ctx.DryRun) { ctx.Log.LogInformation("[dry-run] would remove tag {Tag} from {Name}", tag, ctx.Torrent.Name); return ActionOutcome.WouldApply; }
        await ctx.Qbt.RemoveTagAsync(ctx.Torrent.Hash, tag, ctx.CancellationToken);
        ctx.Log.LogInformation("Removed tag {Tag} from {Name}", tag, ctx.Torrent.Name);
        return ActionOutcome.Applied;
    }
}
