namespace Qbitflow.Engine.Conditions;

/// <summary>How rows of a source type are reached from the torrent a rule is evaluating.</summary>
public enum SourceCorrelation
{
    /// <summary>The torrent itself -- the outer row of every compiled query.</summary>
    Anchor,

    /// <summary>Correlated to the torrent by normalized path_key (every media/history source).</summary>
    PathKey,

    /// <summary>Not correlated to the torrent at all; addressed purely by instance (storage paths).</summary>
    Standalone
}

/// <summary>
/// One addressable source type -- the <c>&lt;type&gt;</c> segment of a field key -- and the fields
/// a condition may reference on it. Instances are runtime configuration and deliberately do not
/// appear here: this catalog is the compile-time half, and the <c>&lt;instance&gt;</c> segment is
/// validated separately against whatever the user has configured.
/// </summary>
public class SourceTypeDefinition
{
    public required string TypeKey { get; init; }
    public required string DisplayName { get; init; }
    public required string TableName { get; init; }
    public required string AliasPrefix { get; init; }
    public required SourceCorrelation Correlation { get; init; }
    public required IReadOnlyDictionary<string, FieldDefinition> Fields { get; init; }
}
