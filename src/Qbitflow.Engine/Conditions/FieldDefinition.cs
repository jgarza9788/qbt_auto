namespace Qbitflow.Engine.Conditions;

/// <summary>
/// One documented field a condition can reference, as the last segment of a
/// <c>&lt;type&gt;.&lt;instance&gt;.&lt;field&gt;</c> key.
///
/// A field is either <em>row-level</em> (it reads a column off one snapshot row, via
/// <see cref="RowExpression"/>) or an <em>aggregate</em> over every correlated row
/// (<see cref="AggregateExpression"/>) -- the compiler needs to know which, because a row field
/// becomes an EXISTS subquery while an aggregate becomes a scalar subquery compared directly.
/// Both use "{alias}" as a placeholder for whatever table alias the compiler assigns.
/// </summary>
public class FieldDefinition
{
    public required string Key { get; init; }
    public required FieldValueType ValueType { get; init; }
    public string? Description { get; init; }
    public string? ExampleValue { get; init; }

    /// <summary>Row-level SQL, e.g. "{alias}.title" or "days_since({alias}.watched_at)". Null on aggregate-only fields.</summary>
    public string? RowExpression { get; init; }

    /// <summary>Aggregate SQL evaluated over the correlated rows, e.g. "COUNT(*)" or "MAX({alias}.watched_at)".</summary>
    public string? AggregateExpression { get; init; }

    /// <summary>Optional wrapper applied to the finished aggregate subquery, using "{inner}" -- e.g. "days_since({inner})".</summary>
    public string? AggregateWrapper { get; init; }

    /// <summary>Restricts the field to rows of one kind ("media" / "history"). Null means it applies to every kind.</summary>
    public string? KindFilter { get; init; }

    public bool IsAggregate => AggregateExpression is not null;

    public string ResolveRow(string alias) =>
        (RowExpression ?? throw new ConditionCompileException($"Field '{Key}' has no row-level form."))
            .Replace("{alias}", alias);

    public string ResolveAggregate(string alias) =>
        (AggregateExpression ?? throw new ConditionCompileException($"Field '{Key}' is not an aggregate."))
            .Replace("{alias}", alias);
}
