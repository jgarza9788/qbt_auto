namespace QbitFlow.Core.Domain;

/// <summary>Minimum effective cadence for any schedule, in seconds.</summary>
public static class ScheduleLimits
{
    public const int MinIntervalSeconds = 300;
}

public class Pipeline
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Default dry-run for scheduled runs; a manual run may override.</summary>
    public bool DryRun { get; set; }

    public ScheduleKind ScheduleKind { get; set; } = ScheduleKind.Interval;
    public int? IntervalSeconds { get; set; } = 900;
    public string? CronExpression { get; set; }
    public string TimeZoneId { get; set; } = "UTC";

    public int MaxParallelism { get; set; } = 8;

    /// <summary>Pipeline-wide default for per-rule short-circuit on match.</summary>
    public bool StopOnFirstMatch { get; set; }

    public DateTimeOffset? LastRunUtc { get; set; }
    public DateTimeOffset? NextRunUtc { get; set; }
    public Guid? LastRunId { get; set; }

    /// <summary>In-flight flag for UI + crash recovery; overlap is enforced in-process by a semaphore.</summary>
    public bool IsRunning { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<PipelineSource> Sources { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];

    /// <summary>Effective interval honouring the 5-minute floor.</summary>
    public int EffectiveIntervalSeconds =>
        Math.Max(IntervalSeconds ?? ScheduleLimits.MinIntervalSeconds, ScheduleLimits.MinIntervalSeconds);
}

public class PipelineSource
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PipelineId { get; set; }
    public Pipeline? Pipeline { get; set; }

    public Guid SourceConnectionId { get; set; }
    public SourceConnection? SourceConnection { get; set; }

    public PipelineSourceRoles Roles { get; set; } = PipelineSourceRoles.Data;
    public int Order { get; set; }
}
