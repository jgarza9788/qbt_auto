namespace QbitFlow.Core.Domain;

public class Rule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = "";

    /// <summary>Position in the single global rule list; lower runs first.</summary>
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Overrides the global <c>StopOnFirstMatch</c> setting when set.</summary>
    public bool? StopOnMatch { get; set; }

    /// <summary>
    /// JSON array of qBittorrent <see cref="SourceConnection"/> ids this rule acts on.
    /// Null or empty = every enabled qBittorrent source.
    /// </summary>
    public string? TargetFilterJson { get; set; }

    /// <summary>
    /// When set, this rule won't fire its action against the same torrent more than once per this
    /// many seconds. Null = no throttle (re-evaluated every engine pass).
    /// </summary>
    public int? CooldownSeconds { get; set; }

    public RuleConditionMode ConditionMode { get; set; } = RuleConditionMode.Raw;

    /// <summary>Verbatim expression when <see cref="ConditionMode"/> is <see cref="RuleConditionMode.Raw"/>.</summary>
    public string? RawExpression { get; set; }

    public Guid? RootGroupId { get; set; }
    public RuleConditionGroup? RootGroup { get; set; }

    /// <summary>The final DynamicExpresso string actually evaluated (compiled from the builder, or the raw text).</summary>
    public string CompiledExpression { get; set; } = "true";
    public DateTimeOffset CompiledUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool CompileValid { get; set; } = true;
    public string? CompileError { get; set; }

    public RuleAction? Action { get; set; }
}

public class RuleConditionGroup
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RuleId { get; set; }
    public Guid? ParentGroupId { get; set; }
    public RuleConditionGroup? ParentGroup { get; set; }

    public ConditionLogic Logic { get; set; } = ConditionLogic.And;
    public int Order { get; set; }

    public List<RuleConditionGroup> Children { get; set; } = [];
    public List<RuleCondition> Conditions { get; set; } = [];
}

public class RuleCondition
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public RuleConditionGroup? Group { get; set; }

    public string Field { get; set; } = "";
    public ConditionOperator Operator { get; set; }
    public ConditionValueKind ValueKind { get; set; } = ConditionValueKind.String;
    public string Value { get; set; } = "";
    public int Order { get; set; }
}

public class RuleAction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RuleId { get; set; }
    public Rule? Rule { get; set; }

    public int Order { get; set; }

    /// <summary>Registry key: tag.sync, tag.add, tag.remove, category.set, torrent.move, speed.limit, script.run, …</summary>
    public string Type { get; set; } = "";

    /// <summary>Action parameters as JSON; values may contain &lt;placeholder&gt; tokens.</summary>
    public string ParamsJson { get; set; } = "{}";
}
