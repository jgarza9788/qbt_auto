namespace QbitFlow.Core.Domain;

public class RunHistory
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public RunTrigger Trigger { get; set; }
    public bool DryRun { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Running;

    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
    public long? DurationMs { get; set; }

    public int TorrentsEvaluated { get; set; }
    public int RulesEvaluated { get; set; }
    public int ActionsApplied { get; set; }
    public int ActionsWouldApply { get; set; }
    public int ActionsSkipped { get; set; }
    public int ErrorCount { get; set; }

    public string? SummaryJson { get; set; }

    public List<RunRuleResult> RuleResults { get; set; } = [];
}

public class RunRuleResult
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RunId { get; set; }
    public RunHistory? Run { get; set; }

    public Guid RuleId { get; set; }
    public string RuleName { get; set; } = "";

    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int ErrorCount { get; set; }
    public int ActionsApplied { get; set; }
    public int ActionsWouldApply { get; set; }
}

public class RunLogEntry
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public long Seq { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Level { get; set; } = "Information";
    public string Message { get; set; } = "";
    public string? TorrentHash { get; set; }
    public Guid? RuleId { get; set; }
}

/// <summary>Backs <c>script.run</c> idempotency in the DB, in addition to the on-disk marker file.</summary>
public class ScriptRunMarker
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RuleId { get; set; }
    public string TorrentHash { get; set; } = "";
    public string RunDir { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
