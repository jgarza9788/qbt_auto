using Microsoft.Extensions.Logging;
using QbitFlow.Core.Domain;
using QbitFlow.Core.Contracts;

namespace QbitFlow.Core.Abstractions;

/// <summary>One field on an action's parameter form (drives the UI and server-side validation).</summary>
public sealed record ActionParam(string Key, string Label, string Kind, bool Required, string? Help = null);

public sealed record ActionParamSchema(string DisplayName, IReadOnlyList<ActionParam> Params);

/// <summary>Everything a handler needs to act on one torrent.</summary>
public sealed class ActionContext
{
    public required Guid RuleId { get; init; }
    public required string RuleName { get; init; }
    public required bool? Match { get; init; }
    public required TorrentView Torrent { get; init; }
    public required IReadOnlyDictionary<string, object?> Fields { get; init; }

    /// <summary>Action parameters with <c>&lt;placeholder&gt;</c> tokens already substituted.</summary>
    public required IReadOnlyDictionary<string, string> Params { get; init; }

    public required IQbtActionTarget Qbt { get; init; }
    public required bool DryRun { get; init; }
    public required ILogger Log { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>Convenience for handlers: has the requested state already been reached?</summary>
    public string Param(string key, string fallback = "") =>
        Params.TryGetValue(key, out var v) ? v : fallback;
}

/// <summary>A torrent event. Adding one is a single new class — discovered by assembly scan.</summary>
public interface IActionHandler
{
    /// <summary>Registry key, e.g. <c>tag.sync</c>, <c>category.set</c>.</summary>
    string Type { get; }

    ActionParamSchema Schema { get; }

    Task<ActionOutcome> ApplyAsync(ActionContext ctx);
}
