using System.Globalization;
using Microsoft.Extensions.Logging;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;

namespace QbitFlow.Engine.Actions;

/// <summary>
/// Runs a shell command in a directory on match, once per (rule, torrent). Idempotency is tracked
/// both on disk (a marker file named after the rule) and in the DB (survives run-dir wipes).
/// Ported from the legacy <c>AutoScript</c>; the Windows shebang check is fixed
/// (a bare <c>cmd.exe</c> resolves via PATH, so no <c>File.Exists</c> gate).
/// </summary>
public sealed class ScriptRunActionHandler(IScriptRunMarkerStore markers) : IActionHandler
{
    public string Type => "script.run";

    public ActionParamSchema Schema => new("Run script",
    [
        new ActionParam("runDir",  "Working directory", "string", Required: true, "Supports <placeholder> tokens."),
        new ActionParam("shebang", "Shebang / interpreter", "string", Required: false, "Default: cmd.exe (Windows) / /bin/bash."),
        new ActionParam("script",  "Script", "string", Required: true),
        new ActionParam("timeout", "Timeout (seconds)", "number", Required: false, "Default 500."),
    ]);

    public async Task<ActionOutcome> ApplyAsync(ActionContext ctx)
    {
        if (ctx.Match != true) return ActionOutcome.NotApplicable;

        var runDir = ResolveRunDir(ctx.Param("runDir"), ctx.Log);
        if (runDir is null) return ActionOutcome.Skipped;

        var shebang = ctx.Param("shebang").Trim();
        if (shebang.Length == 0) shebang = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";

        var script = ctx.Param("script");
        var timeout = TimeSpan.FromSeconds(
            double.TryParse(ctx.Param("timeout", "500"), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 500);

        var diskMarker = Path.Combine(runDir, SanitizeFileName(ctx.RuleName));
        if (File.Exists(diskMarker) || await markers.ExistsAsync(ctx.RuleId, ctx.Torrent.Hash, ctx.CancellationToken))
        {
            ctx.Log.LogDebug("Script already ran for {Rule} / {Name}", ctx.RuleName, ctx.Torrent.Name);
            return ActionOutcome.Skipped;
        }

        if (ctx.DryRun)
        {
            ctx.Log.LogInformation("[dry-run] would run `{Script}` in {Dir} for {Name}", script, runDir, ctx.Torrent.Name);
            return ActionOutcome.WouldApply;
        }

        var result = await ProcessRunner.RunShebangAsync(shebang, script, runDir, timeout, ctx.CancellationToken);
        ctx.Log.LogInformation("Script for {Name}: exit={Exit} timedOut={TimedOut}\n{Out}\n{Err}",
            ctx.Torrent.Name, result.ExitCode, result.TimedOut, result.StdOut.Trim(), result.StdErr.Trim());

        try { await File.WriteAllTextAsync(diskMarker, "", ctx.CancellationToken); } catch { /* best effort */ }
        await markers.AddAsync(ctx.RuleId, ctx.Torrent.Hash, runDir, ctx.CancellationToken);

        return result is { ExitCode: 0, TimedOut: false } ? ActionOutcome.Applied : ActionOutcome.Error;
    }

    private static string? ResolveRunDir(string raw, ILogger log)
    {
        var dir = raw.Trim();
        if (dir.Length == 0) return null;

        for (var i = 0; i < 4 && !Directory.Exists(dir); i++)
            dir = Directory.GetParent(dir)?.FullName ?? dir;

        if (Directory.Exists(dir)) return dir;
        log.LogWarning("script.run: no usable directory for '{Raw}'", raw);
        return null;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length == 0 ? "qbitflow.script.done" : name;
    }
}
