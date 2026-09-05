using System.Text.RegularExpressions;

namespace Qbitflow.Engine.Conditions.AdvancedSql;

/// <summary>
/// Rewrites the visual-builder pseudo-field <c>storage.&lt;name&gt;.&lt;attribute&gt;</c> into the
/// same scalar subquery <see cref="ConditionSqlCompiler"/> emits, so a rule author can use the
/// identical field key whether they're in the visual builder or the advanced-SQL box. Raw SQL
/// has no other way to reach a storage row -- <c>storage.H00.used_percent</c> parses as
/// <c>schema.table.column</c> and fails with "no such column".
/// </summary>
internal static class StorageFieldExpander
{
    // storage.<name>.<attribute> -- <name> excludes '.', whitespace, quotes and parens so we
    // don't run past the token; <attribute> is a bare identifier looked up in the registry.
    private static readonly Regex StorageFieldPattern = new(
        @"\bstorage\.([^\s.'""()]+)\.([A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled);

    public static string Expand(string rawSql) => StorageFieldPattern.Replace(rawSql, match =>
    {
        var name = match.Groups[1].Value;
        var attribute = match.Groups[2].Value;

        if (!SnapshotFieldRegistry.StorageAttributes.TryGetValue(attribute, out var attrDef))
        {
            var valid = string.Join(", ", SnapshotFieldRegistry.StorageAttributes.Keys);
            throw new ConditionCompileException(
                $"Unknown storage attribute '{attribute}' in '{match.Value}'. Valid attributes: {valid}.");
        }

        // The author's threat model is an admin typo, not injection (see AdvancedSqlExecutor),
        // and the surrounding WHERE text is already raw author-controlled SQL -- but a stray
        // quote in a configured path name still shouldn't break the query, so double it.
        var escapedName = name.Replace("'", "''");
        return $"(SELECT {attrDef.Column} FROM storage_paths WHERE name = '{escapedName}')";
    });
}
