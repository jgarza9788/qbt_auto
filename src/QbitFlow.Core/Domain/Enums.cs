namespace QbitFlow.Core.Domain;

public enum SourceKind { Plex = 0, Jellyfin = 1, Qbt = 2 }

public enum SourceAuthMode { UserPassword = 0, ApiKey = 1, PlexToken = 2 }

public enum HealthState { Unknown = 0, Healthy = 1, Degraded = 2, Unreachable = 3 }

public enum ScheduleKind { Interval = 0, Cron = 1 }

[Flags]
public enum PipelineSourceRoles
{
    None = 0,
    Data = 1,
    ActionTarget = 2,
}

public enum RunTrigger { Schedule = 0, Manual = 1, Api = 2 }

public enum RunStatus { Running = 0, Succeeded = 1, Failed = 2, Cancelled = 3 }

public enum RuleConditionMode { Builder = 0, Raw = 1 }

public enum ConditionLogic { And = 0, Or = 1 }

public enum ConditionOperator
{
    Eq, Neq, Gt, Gte, Lt, Lte,
    Contains, NotContains,
    Matches, NotMatches,
    InList,
    DaysAgoGte, DaysAgoLt,
    IsTrue, IsFalse,
}

public enum ConditionValueKind { Number = 0, String = 1, Bool = 2, Regex = 3, Duration = 4, List = 5 }

/// <summary>Result of applying a single <see cref="Abstractions.IActionHandler"/> to one torrent.</summary>
public enum ActionOutcome
{
    /// <summary>The mutation was performed.</summary>
    Applied,
    /// <summary>Dry-run: the mutation would have been performed.</summary>
    WouldApply,
    /// <summary>Criteria matched but the torrent was already in the desired state.</summary>
    Skipped,
    /// <summary>Criteria did not match (or evaluated to null), so nothing to do.</summary>
    NotApplicable,
    /// <summary>An error occurred while applying the action.</summary>
    Error,
}

/// <summary>Field categories surfaced to the rule builder / raw-expression validator.</summary>
public enum FieldSource { Torrent = 0, Drive = 1, Media = 2, Derived = 3 }

public enum FieldType { Number = 0, String = 1, Bool = 2, DateIso = 3, Duration = 4, List = 5 }
