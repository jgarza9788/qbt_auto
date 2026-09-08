using System.Globalization;
using System.Text.Json;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.Conditions;
using Qbitflow.Snapshot;

namespace Qbitflow.Engine.Conditions;

/// <summary>
/// Compiles a structured ConditionNode tree into one parameterized
/// "SELECT instance_id, torrent_hash FROM qbittorrent t WHERE ..." query against the snapshot
/// schema. Every value from the tree is bound as a SQLite parameter and every field key is
/// resolved through <see cref="SourceFieldCatalog"/>, so there is no way for a condition to reach
/// arbitrary SQL text -- not even through the instance segment, which is bound rather than
/// interpolated.
///
/// A rule always resolves to a set of torrents, so <c>qbittorrent</c> is the outer row and every
/// other source is reached from it. A comparison on another source's row field is correlated
/// automatically -- <c>jellyfin.jf1.title contains 'Foo'</c> at the top level becomes an EXISTS
/// over the jellyfin table joined on path_key -- so an explicit <see cref="ExistsNode"/> is only
/// needed when several conditions have to hold on the <em>same</em> row.
/// </summary>
public class ConditionSqlCompiler
{
    /// <summary>The alias of the outer torrent row; every correlation is written against it.</summary>
    private const string AnchorAlias = "t";

    /// <summary>Where a node is being compiled: the outer torrent row, or inside one source's EXISTS subquery.</summary>
    private sealed record Scope(SourceTypeDefinition Type, string Instance, string Alias)
    {
        public bool IsAnchor => Type.Correlation == SourceCorrelation.Anchor && Alias == AnchorAlias;
        public string Source => $"{Type.TypeKey}.{Instance}";
    }

    public CompiledQuery Compile(ConditionNode root, IReadOnlyList<int>? targetInstanceIds = null) =>
        Compile(root, FieldResolutionContext.Lenient, targetInstanceIds);

    public CompiledQuery Compile(ConditionNode root, FieldResolutionContext resolution, IReadOnlyList<int>? targetInstanceIds = null)
    {
        var anchorType = SourceFieldCatalog.Types[SourceNaming.TypeKey(SourceNaming.AnchorType)];
        var ctx = new CompileContext();
        var scope = new Scope(anchorType, SourceNaming.Wildcard, AnchorAlias);
        var predicate = CompileNode(root, scope, resolution, ctx);

        var whereClauses = new List<string> { $"({predicate})" };
        if (targetInstanceIds is { Count: > 0 })
        {
            var placeholders = targetInstanceIds.Select(id => ctx.AddParameter(id));
            whereClauses.Add($"{AnchorAlias}.instance_id IN ({string.Join(",", placeholders)})");
        }

        var sql = $"SELECT DISTINCT {AnchorAlias}.instance_id AS instance_id, {AnchorAlias}.hash AS torrent_hash "
                + $"FROM {anchorType.TableName} {AnchorAlias} WHERE {string.Join(" AND ", whereClauses)}";
        return new CompiledQuery { Sql = sql, Parameters = ctx.Parameters };
    }

    public async Task<List<MatchedTorrent>> ExecuteAsync(SnapshotDatabase snapshot, CompiledQuery query, CancellationToken ct = default)
    {
        using var command = snapshot.Connection.CreateCommand();
        command.CommandText = query.Sql;
        foreach (var (name, value) in query.Parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var results = new List<MatchedTorrent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new MatchedTorrent(reader.GetInt32(0), reader.GetString(1)));
        }
        return results;
    }

    private string CompileNode(ConditionNode node, Scope scope, FieldResolutionContext resolution, CompileContext ctx) => node switch
    {
        GroupNode g => CompileGroup(g, scope, resolution, ctx),
        NotNode n => $"NOT ({CompileNode(n.Child, scope, resolution, ctx)})",
        ComparisonNode c => CompileComparison(c, scope, resolution, ctx),
        ExistsNode e => CompileExists(e, scope, resolution, ctx),
        _ => throw new ConditionCompileException($"Unsupported condition node type '{node.GetType().Name}'.")
    };

    private string CompileGroup(GroupNode g, Scope scope, FieldResolutionContext resolution, CompileContext ctx)
    {
        if (g.Children.Count == 0)
        {
            // Vacuous truth: an empty AND group matches everything, an empty OR group matches nothing.
            return g.Operator == LogicalOperator.And ? "1=1" : "0=1";
        }

        var op = g.Operator == LogicalOperator.And ? " AND " : " OR ";
        var parts = g.Children.Select(c => $"({CompileNode(c, scope, resolution, ctx)})");
        return string.Join(op, parts);
    }

    private string CompileComparison(ComparisonNode c, Scope scope, FieldResolutionContext resolution, CompileContext ctx)
    {
        if (!FieldKey.TryParse(c.Field, out var key, out var parseError))
        {
            throw new ConditionCompileException(parseError!);
        }

        var type = ResolveType(key.Type, c.Field);
        if (!type.Fields.TryGetValue(key.Field, out var field))
        {
            throw new ConditionCompileException(
                $"Unknown field '{key.Field}' for source type '{key.Type}'. Available: {string.Join(", ", type.Fields.Keys.Order())}.");
        }

        ValidateInstance(key.Type, key.Instance, resolution);

        // Inside an explicit EXISTS the rows are already fixed to one source, so a key naming a
        // different one would silently compare against the wrong table.
        if (!scope.IsAnchor)
        {
            if (key.Source != scope.Source)
            {
                throw new ConditionCompileException(
                    $"Field '{c.Field}' does not belong to this check's source '{scope.Source}'.");
            }
            if (field.IsAggregate)
            {
                throw new ConditionCompileException(
                    $"'{c.Field}' is an aggregate over all matching rows; use it on its own rather than inside a related-source check.");
            }
            var kindClause = field.KindFilter is { } kind ? $"{scope.Alias}.kind = {ctx.AddParameter(kind)} AND " : "";
            return kindClause + $"({CompileOperator(field.ResolveRow(scope.Alias), field.ValueType, c, ctx)})";
        }

        return type.Correlation switch
        {
            SourceCorrelation.Anchor => CompileAnchorComparison(c, key, field, resolution, ctx),
            SourceCorrelation.PathKey when field.IsAggregate => CompileAggregateComparison(c, key, type, field, ctx),
            SourceCorrelation.PathKey => CompileCorrelatedComparison(c, key, type, field, ctx),
            SourceCorrelation.Standalone => CompileStandaloneComparison(c, key, type, field, ctx),
            _ => throw new ConditionCompileException($"Source type '{key.Type}' cannot be compared against.")
        };
    }

    /// <summary>A field on the torrent itself. Naming an instance restricts which torrents match.</summary>
    private static string CompileAnchorComparison(ComparisonNode c, FieldKey key, FieldDefinition field, FieldResolutionContext resolution, CompileContext ctx)
    {
        if (key.IsWildcardInstance)
        {
            return CompileOperator(field.ResolveRow(AnchorAlias), field.ValueType, c, ctx);
        }

        // Bound before the value so parameter numbering follows the order they appear in the SQL.
        var instanceParam = ctx.AddParameter(CanonicalName(key, resolution));
        var predicate = CompileOperator(field.ResolveRow(AnchorAlias), field.ValueType, c, ctx);
        return $"{AnchorAlias}.instance = {instanceParam} AND ({predicate})";
    }

    /// <summary>
    /// A row field on a correlated source. Compiled as an EXISTS over that source's rows joined to
    /// the torrent by path_key, so "any matching row satisfies it" -- the same thing an explicit
    /// related-source check does, without making the author build one for a single comparison.
    /// </summary>
    private static string CompileCorrelatedComparison(ComparisonNode c, FieldKey key, SourceTypeDefinition type, FieldDefinition field, CompileContext ctx)
    {
        var alias = ctx.NextAlias(type.AliasPrefix);
        var where = CorrelationClauses(key, type, alias, ctx, field);
        where.Add($"({CompileOperator(field.ResolveRow(alias), field.ValueType, c, ctx)})");
        return $"EXISTS (SELECT 1 FROM {type.TableName} {alias} WHERE {string.Join(" AND ", where)})";
    }

    /// <summary>
    /// An aggregate over the correlated rows (play_count, last_watched_at, ...). It carries its own
    /// correlation, so it compiles to a scalar subquery compared directly -- which is what makes
    /// "never watched" the plain <c>play_count = 0</c> rather than a negated EXISTS.
    /// </summary>
    private static string CompileAggregateComparison(ComparisonNode c, FieldKey key, SourceTypeDefinition type, FieldDefinition field, CompileContext ctx)
    {
        var alias = ctx.NextAlias(type.AliasPrefix);
        var where = CorrelationClauses(key, type, alias, ctx, field);
        var inner = $"(SELECT {field.ResolveAggregate(alias)} FROM {type.TableName} {alias} WHERE {string.Join(" AND ", where)})";
        var expr = field.AggregateWrapper is { } wrapper ? wrapper.Replace("{inner}", inner) : inner;
        return CompileOperator(expr, field.ValueType, c, ctx);
    }

    /// <summary>A field on a source that isn't tied to a torrent at all -- storage paths.</summary>
    private static string CompileStandaloneComparison(ComparisonNode c, FieldKey key, SourceTypeDefinition type, FieldDefinition field, CompileContext ctx)
    {
        var alias = ctx.NextAlias(type.AliasPrefix);
        var where = new List<string>();
        if (!key.IsWildcardInstance)
        {
            where.Add($"{alias}.instance = {ctx.AddParameter(key.Instance)}");
        }
        where.Add($"({CompileOperator(field.ResolveRow(alias), field.ValueType, c, ctx)})");
        return $"EXISTS (SELECT 1 FROM {type.TableName} {alias} WHERE {string.Join(" AND ", where)})";
    }

    private string CompileExists(ExistsNode e, Scope scope, FieldResolutionContext resolution, CompileContext ctx)
    {
        if (!scope.IsAnchor)
        {
            throw new ConditionCompileException("A related-source check cannot be nested inside another one.");
        }

        if (!FieldKey.TryParseSource(e.Source, out var typeKey, out var instance, out var sourceError))
        {
            throw new ConditionCompileException(sourceError!);
        }

        var type = ResolveType(typeKey, e.Source);
        if (type.Correlation == SourceCorrelation.Anchor)
        {
            throw new ConditionCompileException(
                $"'{e.Source}' is the torrent itself; compare its fields directly rather than through a related-source check.");
        }

        ValidateInstance(typeKey, instance, resolution);

        var alias = ctx.NextAlias(type.AliasPrefix);

        // The instance is bound before the inner predicate is compiled so parameter numbering
        // follows the order the placeholders appear in the emitted SQL.
        var instanceParam = instance == SourceNaming.Wildcard ? null : ctx.AddParameter(instance);
        var innerScope = new Scope(type, instance, alias);
        var innerPredicate = CompileNode(e.Condition, innerScope, resolution, ctx);

        var where = new List<string>();
        if (type.Correlation == SourceCorrelation.PathKey)
        {
            // Plain equality, not the path_matches() UDF: both sides are already normalized by the
            // same PathKeyNormalizer at ingest, so exact match is correct here, and unlike a UDF
            // call it lets ix_<type>_path_key drive an index seek per outer row instead of a full
            // O(N*M) scan with a managed callback per comparison -- the difference is 34s vs a few
            // ms at 10k torrents (see BenchmarkTests). path_matches() stays available for
            // advanced-mode SQL and explicit substring-tolerant comparisons.
            where.Add($"{alias}.path_key = {AnchorAlias}.path_key");
        }
        if (instanceParam is not null)
        {
            where.Add($"{alias}.instance = {instanceParam}");
        }
        where.Add($"({innerPredicate})");

        var prefix = e.Negate ? "NOT EXISTS" : "EXISTS";
        return $"{prefix} (SELECT 1 FROM {type.TableName} {alias} WHERE {string.Join(" AND ", where)})";
    }

    /// <summary>The path_key correlation, instance and kind filters shared by every correlated subquery.</summary>
    private static List<string> CorrelationClauses(FieldKey key, SourceTypeDefinition type, string alias, CompileContext ctx, FieldDefinition field)
    {
        var where = new List<string> { $"{alias}.path_key = {AnchorAlias}.path_key" };
        if (!key.IsWildcardInstance)
        {
            where.Add($"{alias}.instance = {ctx.AddParameter(key.Instance)}");
        }
        if (field.KindFilter is { } kind)
        {
            where.Add($"{alias}.kind = {ctx.AddParameter(kind)}");
        }
        return where;
    }

    private static SourceTypeDefinition ResolveType(string typeKey, string reference)
    {
        if (SourceFieldCatalog.Types.TryGetValue(typeKey, out var type))
        {
            return type;
        }
        throw new ConditionCompileException(
            $"Unknown source type '{typeKey}' in '{reference}'. Valid types: {string.Join(", ", SourceFieldCatalog.Types.Keys)}.");
    }

    private static void ValidateInstance(string typeKey, string instance, FieldResolutionContext resolution)
    {
        if (instance == SourceNaming.Wildcard || !resolution.ValidatesInstances)
        {
            return;
        }

        var configured = resolution.NamesFor(typeKey);
        if (configured.Any(n => string.Equals(n, instance, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Instance names are globally unique, so if the name exists at all we can say what it is.
        if (resolution.TypeOf(instance) is { } actualType)
        {
            throw new ConditionCompileException(
                $"'{instance}' is a {actualType} instance, not a {typeKey} instance.");
        }

        var known = configured.Count > 0 ? string.Join(", ", configured) : "(none configured)";
        throw new ConditionCompileException(
            $"No {typeKey} instance named '{instance}'. Configured: {known}. Use '{typeKey}.{SourceNaming.Wildcard}' to match any.");
    }

    private static string CanonicalName(FieldKey key, FieldResolutionContext resolution) =>
        resolution.NamesFor(key.Type).FirstOrDefault(n => string.Equals(n, key.Instance, StringComparison.OrdinalIgnoreCase))
        ?? key.Instance;

    private static string CompileOperator(string expr, FieldValueType valueType, ComparisonNode c, CompileContext ctx)
    {
        switch (c.Operator)
        {
            case ComparisonOperator.IsNull:
                return $"{expr} IS NULL";

            case ComparisonOperator.IsNotNull:
                return $"{expr} IS NOT NULL";

            case ComparisonOperator.In:
            case ComparisonOperator.NotIn:
            {
                if (c.Value is not { ValueKind: JsonValueKind.Array } arr)
                {
                    throw new ConditionCompileException($"Field '{c.Field}': {c.Operator} requires an array value.");
                }

                var placeholders = arr.EnumerateArray().Select(item => ctx.AddParameter(ConvertValue(item, valueType, c.Field))).ToList();
                if (placeholders.Count == 0)
                {
                    return c.Operator == ComparisonOperator.In ? "0=1" : "1=1";
                }

                var op = c.Operator == ComparisonOperator.In ? "IN" : "NOT IN";
                return $"{expr} {op} ({string.Join(",", placeholders)})";
            }

            case ComparisonOperator.Contains:
            {
                if (valueType != FieldValueType.Text)
                {
                    throw new ConditionCompileException($"Field '{c.Field}': Contains only applies to text fields.");
                }

                var value = RequireScalarString(c);
                var param = ctx.AddParameter($"%{value}%");
                return $"{expr} LIKE {param}";
            }

            case ComparisonOperator.Like:
            case ComparisonOperator.NotLike:
            {
                var value = RequireScalarString(c);
                var param = ctx.AddParameter(value);
                var op = c.Operator == ComparisonOperator.Like ? "LIKE" : "NOT LIKE";
                return $"{expr} {op} {param}";
            }

            case ComparisonOperator.Matches:
            case ComparisonOperator.NotMatches:
            {
                if (valueType != FieldValueType.Text)
                {
                    throw new ConditionCompileException($"Field '{c.Field}': {c.Operator} only applies to text fields.");
                }

                var value = RequireScalarString(c);
                var param = ctx.AddParameter(value);
                var op = c.Operator == ComparisonOperator.Matches ? "REGEXP" : "NOT REGEXP";
                return $"{expr} {op} {param}";
            }

            default:
            {
                var sqlOp = c.Operator switch
                {
                    ComparisonOperator.Eq => "=",
                    ComparisonOperator.Ne => "!=",
                    ComparisonOperator.Gt => ">",
                    ComparisonOperator.Gte => ">=",
                    ComparisonOperator.Lt => "<",
                    ComparisonOperator.Lte => "<=",
                    _ => throw new ConditionCompileException($"Unsupported operator {c.Operator}.")
                };

                if (c.Value is not { } value)
                {
                    throw new ConditionCompileException($"Field '{c.Field}': operator {c.Operator} requires a value.");
                }

                var param = ctx.AddParameter(ConvertValue(value, valueType, c.Field));
                return $"{expr} {sqlOp} {param}";
            }
        }
    }

    private static string RequireScalarString(ComparisonNode c)
    {
        if (c.Value is not { ValueKind: JsonValueKind.String } v)
        {
            throw new ConditionCompileException($"Field '{c.Field}': {c.Operator} requires a string value.");
        }
        return v.GetString()!;
    }

    private static object ConvertValue(JsonElement value, FieldValueType type, string fieldKey) => type switch
    {
        FieldValueType.Integer when value.ValueKind == JsonValueKind.Number => value.GetInt64(),
        FieldValueType.Real when value.ValueKind == JsonValueKind.Number => value.GetDouble(),
        FieldValueType.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean() ? 1L : 0L,
        FieldValueType.DateTime when value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) => dt.ToString("o"),
        FieldValueType.Text when value.ValueKind == JsonValueKind.String => value.GetString()!,
        _ => throw new ConditionCompileException($"Field '{fieldKey}': value does not match the expected type {type}.")
    };
}
