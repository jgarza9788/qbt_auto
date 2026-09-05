namespace Qbitflow.Engine;

/// <summary>
/// The editable fields of a rule as posted from the editor form. Lets a dry-run preview
/// evaluate the rule <em>as currently typed</em> -- including unsaved changes -- without
/// touching the persisted <c>Rule</c> or writing a <c>RunRecord</c>.
/// </summary>
public sealed record RuleDraft(
    string ConditionTreeJson,
    bool UseAdvancedSql,
    string? AdvancedSqlWhere,
    string ActionsJson,
    IReadOnlyList<int> TargetInstanceIds);

/// <summary>One action's would-happen tally from a dry run.</summary>
public sealed record PreviewActionLine(string Description, int WouldChange, int AlreadyMatching, int Failed);

/// <summary>
/// Result of <see cref="IRuleRunner.DryRunAsync"/>: what the rule would match and do right
/// now, with nothing actually applied.
/// </summary>
public sealed record RulePreview(
    bool Ok,
    int MatchedCount,
    int TorrentsInSnapshot,
    IReadOnlyList<PreviewActionLine> Actions,
    IReadOnlyList<string> SampleMatchedHashes,
    string? Error)
{
    public static RulePreview Failure(string error) => new(false, 0, 0, [], [], error);
}
