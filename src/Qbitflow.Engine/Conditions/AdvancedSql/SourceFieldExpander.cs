using System.Text;
using Qbitflow.Core.Domain;

namespace Qbitflow.Engine.Conditions.AdvancedSql;

/// <summary>
/// Rewrites <c>&lt;type&gt;.&lt;instance&gt;.&lt;field&gt;</c> keys inside author-written SQL into the
/// same SQL <see cref="ConditionSqlCompiler"/> emits, so a key copied from the field reference panel
/// works verbatim in the advanced-SQL box. Without this, <c>jellyfin.jf1.title</c> parses as
/// <c>schema.table.column</c> and fails with "no such column".
///
/// The scan skips string literals, quoted/bracketed identifiers and comments, and only rewrites a
/// dotted run of exactly three segments whose first segment is a known source type -- so
/// <c>t.category</c>, <c>main.qbittorrent.hash</c> and an author's own subquery aliases are all left
/// alone. A run that *does* start with a known type but names a bad instance or field throws, which
/// is a far better error than SQLite's.
///
/// Instance names are interpolated as escaped literals rather than bound: advanced SQL has no
/// parameter-binding path, the surrounding text is already raw author-controlled SQL, and the threat
/// model is an admin typo (see <see cref="AdvancedSqlExecutor"/>). Names are also validated against
/// the configured instances first, and <see cref="SourceNaming.NamePattern"/> excludes quotes.
/// </summary>
internal static class SourceFieldExpander
{
    /// <summary>The alias AdvancedSqlExecutor gives the outer torrent row in WhereClause mode.</summary>
    private const string AnchorAlias = "t";

    public static string Expand(string rawSql, AdvancedSqlMode mode, FieldResolutionContext resolution)
    {
        var sb = new StringBuilder(rawSql.Length + 64);
        var i = 0;
        var n = rawSql.Length;
        var subqueryCounter = 0;

        while (i < n)
        {
            var c = rawSql[i];

            // Single-quoted string literal ('' is an escaped quote).
            if (c == '\'')
            {
                var start = i++;
                while (i < n)
                {
                    if (rawSql[i] == '\'')
                    {
                        if (i + 1 < n && rawSql[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    i++;
                }
                sb.Append(rawSql, start, i - start);
                continue;
            }

            // Quoted ("...") or bracketed ([...]) identifier.
            if (c == '"' || c == '[')
            {
                var close = c == '"' ? '"' : ']';
                var start = i++;
                while (i < n && rawSql[i] != close)
                {
                    i++;
                }
                if (i < n)
                {
                    i++;
                }
                sb.Append(rawSql, start, i - start);
                continue;
            }

            // -- line comment
            if (c == '-' && i + 1 < n && rawSql[i + 1] == '-')
            {
                var start = i;
                while (i < n && rawSql[i] != '\n')
                {
                    i++;
                }
                sb.Append(rawSql, start, i - start);
                continue;
            }

            // /* block comment */
            if (c == '/' && i + 1 < n && rawSql[i + 1] == '*')
            {
                var start = i;
                i += 2;
                while (i < n && !(rawSql[i] == '*' && i + 1 < n && rawSql[i + 1] == '/'))
                {
                    i++;
                }
                if (i < n)
                {
                    i += 2;
                }
                sb.Append(rawSql, start, i - start);
                continue;
            }

            // A dotted run of identifiers -- the only thing that might be a field key. Consumed
            // whole before deciding, so qualification is part of the candidate rather than
            // something to guard against.
            if (IsIdentStart(c))
            {
                var start = i;
                var segments = new List<string> { ReadSegment(rawSql, ref i) };
                while (i + 1 < n && rawSql[i] == '.' && IsSegmentStart(rawSql[i + 1]))
                {
                    i++;
                    segments.Add(ReadSegment(rawSql, ref i));
                }

                var raw = rawSql[start..i];
                if (segments.Count == 3 && SourceFieldCatalog.Types.ContainsKey(segments[0]))
                {
                    sb.Append(ExpandKey(new FieldKey(segments[0], segments[1], segments[2]), raw, mode, resolution, ref subqueryCounter));
                }
                else
                {
                    sb.Append(raw);
                }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string ExpandKey(FieldKey key, string raw, AdvancedSqlMode mode, FieldResolutionContext resolution, ref int counter)
    {
        var type = SourceFieldCatalog.Types[key.Type];
        if (!type.Fields.TryGetValue(key.Field, out var field))
        {
            throw new ConditionCompileException(
                $"Unknown field '{key.Field}' for source type '{key.Type}' in '{raw}'. Available: {string.Join(", ", type.Fields.Keys.Order())}.");
        }

        ValidateInstance(key, raw, resolution);

        // A full query has no guaranteed outer torrent alias, so only the keys that need no
        // correlation can be expanded there -- the same boundary the storage rewriter had.
        if (mode != AdvancedSqlMode.WhereClause && type.Correlation != SourceCorrelation.Standalone)
        {
            return raw;
        }

        var alias = $"x{counter++}";

        switch (type.Correlation)
        {
            case SourceCorrelation.Standalone:
            {
                if (key.IsWildcardInstance)
                {
                    throw new ConditionCompileException(
                        $"'{raw}' must name one storage path in SQL mode; write your own EXISTS over the {type.TableName} table to check any of them.");
                }
                return $"(SELECT {field.ResolveRow(type.TableName)} FROM {type.TableName} WHERE instance = {Literal(key.Instance)})";
            }

            case SourceCorrelation.Anchor:
            {
                var expr = field.ResolveRow(AnchorAlias);
                // Naming an instance yields NULL on torrents from any other one, so the author's
                // surrounding comparison is simply false there -- composable inside arbitrary SQL.
                return key.IsWildcardInstance
                    ? $"({expr})"
                    : $"(CASE WHEN {AnchorAlias}.instance = {Literal(key.Instance)} THEN {expr} END)";
            }

            default:
            {
                if (!field.IsAggregate)
                {
                    throw new ConditionCompileException(
                        $"'{raw}' is a per-row field and has no single value for a torrent. In SQL mode use an aggregate "
                        + $"({string.Join(", ", type.Fields.Values.Where(f => f.IsAggregate).Select(f => f.Key))}), "
                        + $"or write your own EXISTS over the {type.TableName} table.");
                }

                var where = new List<string> { $"{alias}.path_key = {AnchorAlias}.path_key" };
                if (!key.IsWildcardInstance)
                {
                    where.Add($"{alias}.instance = {Literal(key.Instance)}");
                }
                if (field.KindFilter is { } kind)
                {
                    where.Add($"{alias}.kind = {Literal(kind)}");
                }

                var inner = $"(SELECT {field.ResolveAggregate(alias)} FROM {type.TableName} {alias} WHERE {string.Join(" AND ", where)})";
                return field.AggregateWrapper is { } wrapper ? wrapper.Replace("{inner}", inner) : inner;
            }
        }
    }

    private static void ValidateInstance(FieldKey key, string raw, FieldResolutionContext resolution)
    {
        if (key.IsWildcardInstance || !resolution.ValidatesInstances)
        {
            return;
        }

        var configured = resolution.NamesFor(key.Type);
        if (configured.Any(n => string.Equals(n, key.Instance, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (resolution.TypeOf(key.Instance) is { } actualType)
        {
            throw new ConditionCompileException($"'{key.Instance}' in '{raw}' is a {actualType} instance, not a {key.Type} instance.");
        }

        var known = configured.Count > 0 ? string.Join(", ", configured) : "(none configured)";
        throw new ConditionCompileException(
            $"No {key.Type} instance named '{key.Instance}' in '{raw}'. Configured: {known}.");
    }

    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsSegmentStart(char c) => IsIdentStart(c) || c == '*';

    private static string ReadSegment(string sql, ref int i)
    {
        if (sql[i] == '*')
        {
            i++;
            return SourceNaming.Wildcard;
        }

        var start = i;
        while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '-'))
        {
            i++;
        }
        return sql[start..i];
    }
}
