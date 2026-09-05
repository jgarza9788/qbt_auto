namespace Qbitflow.Core.Domain;

/// <summary>
/// An AutoRule: a cron schedule, a query (condition tree or advanced SQL), a list of
/// actions, and the qBittorrent instance(s) those actions apply to.
///
/// The condition tree, actions, and target instance list are stored as JSON blobs at
/// this persistence layer; Qbitflow.Engine owns the strongly-typed shapes and the
/// tree-to-SQL compiler (added in a later phase).
/// </summary>
public class Rule
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Lower runs first. Ties broken by Id.</summary>
    public int Priority { get; set; }

    /// <summary>If true, a torrent matched by this rule is excluded from lower-priority rules in the same cycle.</summary>
    public bool StopOnMatch { get; set; }

    /// <summary>Preview matches/actions without executing them.</summary>
    public bool DryRun { get; set; }

    public required string CronExpression { get; set; }
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>Structured condition tree, JSON-encoded.</summary>
    public required string ConditionTreeJson { get; set; }

    /// <summary>If UseAdvancedSql is true, this raw SQL WHERE/query is used instead of ConditionTreeJson.</summary>
    public string? AdvancedSqlWhere { get; set; }

    public bool UseAdvancedSql { get; set; }

    /// <summary>List of action definitions, JSON-encoded.</summary>
    public required string ActionsJson { get; set; }

    /// <summary>JSON array of target Instance ids (qBittorrent instances the actions apply to).</summary>
    public required string TargetInstanceIdsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Last time this rule's cron fired (used as the "from" point for computing the next due occurrence -- restart-safe, so a restart never causes a burst of missed-schedule catch-up runs).</summary>
    public DateTimeOffset? LastRunAt { get; set; }
}
