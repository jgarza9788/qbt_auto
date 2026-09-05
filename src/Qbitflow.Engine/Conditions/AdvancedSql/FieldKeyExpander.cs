using System.Text;

namespace Qbitflow.Engine.Conditions.AdvancedSql;

/// <summary>
/// Rewrites the visual-builder's <em>computed</em> torrent field keys (<c>active_days</c>,
/// <c>size_gb</c>, <c>eta_hours</c>, <c>download_speed_bps</c>, ...) into the same SQL the
/// structured <see cref="ConditionSqlCompiler"/> emits, so the keys listed in the Field
/// reference panel work verbatim in the advanced-SQL WHERE box too -- previously a key like
/// <c>active_days</c> fell straight through to SQLite and failed with "no such column".
///
/// Only keys whose registry expression is something other than a bare <c>t.&lt;key&gt;</c>
/// column reference are rewritten: the plain columns (<c>category</c>, <c>ratio</c>, ...)
/// already resolve on their own against the <c>torrents t</c> wrapper, so touching them would
/// only add noise to the compiled-SQL preview. The alias is hard-coded to <c>t</c> because
/// this only runs for <see cref="AdvancedSqlMode.WhereClause"/>, where the executor always
/// wraps the predicate in "FROM torrents t WHERE ...".
///
/// The scan skips string literals, quoted/bracketed identifiers and comments, and never
/// rewrites a token that is qualified (<c>x.active_days</c>) or used as a call
/// (<c>size_gb(...)</c>), so an author who spells out the underlying expression themselves is
/// left alone.
/// </summary>
internal static class FieldKeyExpander
{
    private const string Alias = "t";

    private static readonly IReadOnlyDictionary<string, string> ExpandableKeys = BuildExpandableKeys();

    private static Dictionary<string, string> BuildExpandableKeys()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in SnapshotFieldRegistry.Relations["torrents"].Fields.Values)
        {
            var resolved = field.Resolve(Alias);
            if (!string.Equals(resolved, $"{Alias}.{field.Key}", StringComparison.Ordinal))
            {
                map[field.Key] = resolved;
            }
        }
        return map;
    }

    public static string Expand(string rawSql)
    {
        var sb = new StringBuilder(rawSql.Length + 32);
        var i = 0;
        var n = rawSql.Length;
        var lastSignificant = '\0';

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
                lastSignificant = '\'';
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
                lastSignificant = close;
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

            // Bare identifier -- the only thing we might rewrite.
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(rawSql[i]) || rawSql[i] == '_'))
                {
                    i++;
                }
                var ident = rawSql.Substring(start, i - start);

                var j = i;
                while (j < n && char.IsWhiteSpace(rawSql[j]))
                {
                    j++;
                }
                var isCall = j < n && rawSql[j] == '(';
                var isQualified = lastSignificant == '.';

                if (!isCall && !isQualified && ExpandableKeys.TryGetValue(ident, out var expansion))
                {
                    sb.Append('(').Append(expansion).Append(')');
                }
                else
                {
                    sb.Append(ident);
                }
                lastSignificant = ident[^1];
                continue;
            }

            sb.Append(c);
            if (!char.IsWhiteSpace(c))
            {
                lastSignificant = c;
            }
            i++;
        }

        return sb.ToString();
    }
}
