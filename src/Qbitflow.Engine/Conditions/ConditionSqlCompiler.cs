using System.Globalization;
using System.Text.Json;
using Qbitflow.Core.Domain.Conditions;
using Qbitflow.Snapshot;

namespace Qbitflow.Engine.Conditions;

/// <summary>
/// Compiles a structured ConditionNode tree into one parameterized
/// "SELECT instance_id, torrent_hash FROM torrents t WHERE ..." query against the
/// snapshot schema. Every value from the tree is bound as a SQLite parameter -- field
/// keys are resolved only through SnapshotFieldRegistry, so there is no way for a
/// condition to reach arbitrary SQL text.
/// </summary>
public class ConditionSqlCompiler
{
    public CompiledQuery Compile(ConditionNode root, IReadOnlyList<int>? targetInstanceIds = null)
    {
        var ctx = new CompileContext();
        var predicate = CompileNode(root, "torrents", "t", ctx);

        var whereClauses = new List<string> { $"({predicate})" };
        if (targetInstanceIds is { Count: > 0 })
        {
            var placeholders = targetInstanceIds.Select(id => ctx.AddParameter(id));
            whereClauses.Add($"t.instance_id IN ({string.Join(",", placeholders)})");
        }

        var sql = $"SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE {string.Join(" AND ", whereClauses)}";
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

    private string CompileNode(ConditionNode node, string relation, string alias, CompileContext ctx) => node switch
    {
        GroupNode g => CompileGroup(g, relation, alias, ctx),
        NotNode n => $"NOT ({CompileNode(n.Child, relation, alias, ctx)})",
        ComparisonNode c => CompileComparison(c, relation, alias, ctx),
        ExistsNode e => CompileExists(e, alias, ctx),
        _ => throw new ConditionCompileException($"Unsupported condition node type '{node.GetType().Name}'.")
    };

    private string CompileGroup(GroupNode g, string relation, string alias, CompileContext ctx)
    {
        if (g.Children.Count == 0)
        {
            // Vacuous truth: an empty AND group matches everything, an empty OR group matches nothing.
            return g.Operator == LogicalOperator.And ? "1=1" : "0=1";
        }

        var op = g.Operator == LogicalOperator.And ? " AND " : " OR ";
        var parts = g.Children.Select(c => $"({CompileNode(c, relation, alias, ctx)})");
        return string.Join(op, parts);
    }

    private string CompileExists(ExistsNode e, string outerAlias, CompileContext ctx)
    {
        if (!SnapshotFieldRegistry.Relations.TryGetValue(e.Relation, out var relDef))
        {
            throw new ConditionCompileException($"Unknown relation '{e.Relation}'.");
        }

        var innerAlias = ctx.NextAlias(relDef.AliasPrefix);
        var innerPredicate = CompileNode(e.Condition, e.Relation, innerAlias, ctx);

        // Plain equality, not the path_matches() UDF: both sides are already normalized by
        // the same PathKeyNormalizer at ingest, so exact match is correct here, and unlike a
        // UDF call it lets SQLite use ix_watch_history_path_key/ix_media_items_path_key for
        // an index seek per outer row instead of a full O(N*M) scan with a managed callback
        // per comparison -- the difference is 34s vs a few ms at 10k torrents (see
        // BenchmarkTests). path_matches() stays available for advanced-mode SQL and explicit
        // substring-tolerant comparisons; it's just not the default correlation here.
        var correlation = $"{innerAlias}.path_key = {outerAlias}.path_key";
        var prefix = e.Negate ? "NOT EXISTS" : "EXISTS";

        return $"{prefix} (SELECT 1 FROM {relDef.TableName} {innerAlias} WHERE {correlation} AND ({innerPredicate}))";
    }

    private string CompileComparison(ComparisonNode c, string relation, string alias, CompileContext ctx)
    {
        if (relation == "torrents" && c.Field.StartsWith("storage.", StringComparison.Ordinal))
        {
            return CompileStorageComparison(c, ctx);
        }

        if (!SnapshotFieldRegistry.Relations.TryGetValue(relation, out var relDef) || !relDef.Fields.TryGetValue(c.Field, out var fieldDef))
        {
            throw new ConditionCompileException($"Unknown field '{c.Field}' for relation '{relation}'.");
        }

        return CompileOperator(fieldDef.Resolve(alias), fieldDef.ValueType, c, ctx);
    }

    private string CompileStorageComparison(ComparisonNode c, CompileContext ctx)
    {
        var parts = c.Field.Split('.', 3);
        if (parts.Length != 3)
        {
            throw new ConditionCompileException($"Invalid storage field '{c.Field}'; expected 'storage.<name>.<attribute>'.");
        }

        var storageName = parts[1];
        var attribute = parts[2];

        if (!SnapshotFieldRegistry.StorageAttributes.TryGetValue(attribute, out var attrDef))
        {
            throw new ConditionCompileException($"Unknown storage attribute '{attribute}'.");
        }

        var nameParam = ctx.AddParameter(storageName);
        var expr = $"(SELECT {attrDef.Column} FROM storage_paths WHERE name = {nameParam})";
        return CompileOperator(expr, attrDef.ValueType, c, ctx);
    }

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
