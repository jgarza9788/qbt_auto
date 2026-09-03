using Microsoft.Extensions.Logging;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;

namespace QbitFlow.Engine.Actions;

/// <summary>Resume (start seeding) on match.</summary>
public sealed class SeedingStartActionHandler : IActionHandler
{
    public string Type => "seeding.start";
    public ActionParamSchema Schema => new("Start seeding", []);

    public async Task<ActionOutcome> ApplyAsync(ActionContext ctx)
    {
        if (ctx.Match != true) return ActionOutcome.NotApplicable;
        if (!IsPaused(ctx.Torrent.State)) return ActionOutcome.Skipped;

        if (ctx.DryRun) { ctx.Log.LogInformation("[dry-run] would resume {Name}", ctx.Torrent.Name); return ActionOutcome.WouldApply; }
        await ctx.Qbt.ResumeAsync(ctx.Torrent.Hash, ctx.CancellationToken);
        ctx.Log.LogInformation("Resumed {Name}", ctx.Torrent.Name);
        return ActionOutcome.Applied;
    }

    internal static bool IsPaused(string state) =>
        state.Contains("paused", StringComparison.OrdinalIgnoreCase) ||
        state.Contains("stopped", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Pause (stop seeding) on match.</summary>
public sealed class SeedingStopActionHandler : IActionHandler
{
    public string Type => "seeding.stop";
    public ActionParamSchema Schema => new("Stop seeding", []);

    public async Task<ActionOutcome> ApplyAsync(ActionContext ctx)
    {
        if (ctx.Match != true) return ActionOutcome.NotApplicable;
        if (SeedingStartActionHandler.IsPaused(ctx.Torrent.State)) return ActionOutcome.Skipped;

        if (ctx.DryRun) { ctx.Log.LogInformation("[dry-run] would pause {Name}", ctx.Torrent.Name); return ActionOutcome.WouldApply; }
        await ctx.Qbt.PauseAsync(ctx.Torrent.Hash, ctx.CancellationToken);
        ctx.Log.LogInformation("Paused {Name}", ctx.Torrent.Name);
        return ActionOutcome.Applied;
    }
}

/// <summary>Toggle force-start on match.</summary>
public sealed class SeedingForceStartActionHandler : IActionHandler
{
    public string Type => "seeding.forceStart";
    public ActionParamSchema Schema => new("Force start",
        [new ActionParam("on", "On", "bool", Required: false, "true (default) or false")]);

    public async Task<ActionOutcome> ApplyAsync(ActionContext ctx)
    {
        if (ctx.Match != true) return ActionOutcome.NotApplicable;

        var on = !string.Equals(ctx.Param("on", "true"), "false", StringComparison.OrdinalIgnoreCase);
        if (ctx.Torrent.ForceStart == on) return ActionOutcome.Skipped;

        if (ctx.DryRun) { ctx.Log.LogInformation("[dry-run] would set force-start={On} on {Name}", on, ctx.Torrent.Name); return ActionOutcome.WouldApply; }
        await ctx.Qbt.SetForceStartAsync(ctx.Torrent.Hash, on, ctx.CancellationToken);
        ctx.Log.LogInformation("Set force-start={On} on {Name}", on, ctx.Torrent.Name);
        return ActionOutcome.Applied;
    }
}
