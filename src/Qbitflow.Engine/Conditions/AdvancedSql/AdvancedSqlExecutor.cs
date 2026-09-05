using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Qbitflow.Snapshot;

namespace Qbitflow.Engine.Conditions.AdvancedSql;

/// <summary>
/// Runs power-user-authored raw SQL (Rule.AdvancedSqlWhere) against the snapshot.
/// The threat model here is a mistake by the app's own admin, not an external attacker
/// (only an authenticated admin can ever author a rule) -- so the goal is to stop a typo
/// from corrupting the snapshot or hanging the app, not to sandbox a hostile author:
///   - a second connection to the same shared-cache in-memory DB with PRAGMA query_only
///     set (SQLite's own engine rejects any write statement on that connection with
///     SQLITE_READONLY -- this is enforced by SQLite itself, not application code, though
///     note it's a per-connection pragma, not an OS-level read-only file open: a
///     shared-cache in-memory database can only be addressed via mode=memory, which
///     can't be combined with the URI mode=ro flag, so query_only is the mechanism SQLite
///     actually offers for this)
///   - single-statement-only + a keyword denylist, rejecting attempts to stack additional
///     statements or invoke schema/pragma/write statements
///   - a row cap enforced by the read loop itself (not just a LIMIT clause the author
///     could omit or a FullQuery could paper over)
///   - a cooperative-cancellation timeout on execution
///   - EXPLAIN QUERY PLAN (and, for FullQuery, a LIMIT 0 column probe) run at save time,
///     so a broken query is caught before it's persisted, not at the next scheduled run
/// </summary>
public class AdvancedSqlExecutor
{
    public const int MaxRows = 50_000;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex ForbiddenKeywordPattern = new(
        @"\b(ATTACH|DETACH|PRAGMA|VACUUM|INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|REPLACE|REINDEX)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AdvancedSqlValidationResult Validate(SnapshotDatabase snapshot, string rawSql, AdvancedSqlMode mode)
    {
        var shapeError = ValidateShape(rawSql);
        if (shapeError is not null)
        {
            return AdvancedSqlValidationResult.Failure(shapeError);
        }

        string expandedSql;
        try
        {
            // storage.<name>.<attr> is a visual-builder field key with no raw-SQL equivalent;
            // rewrite it to the scalar subquery the structured compiler uses so the same key
            // works in both editors.
            expandedSql = StorageFieldExpander.Expand(rawSql);
        }
        catch (ConditionCompileException ex)
        {
            return AdvancedSqlValidationResult.Failure(ex.Message);
        }

        var trimmed = expandedSql.Trim().TrimEnd(';');
        var compiledSql = mode == AdvancedSqlMode.WhereClause
            ? $"SELECT DISTINCT t.instance_id AS instance_id, t.hash AS torrent_hash FROM torrents t WHERE {trimmed}"
            : trimmed;

        using var readOnly = OpenReadOnly(snapshot);

        try
        {
            using (var explainCmd = readOnly.CreateCommand())
            {
                explainCmd.CommandText = $"EXPLAIN QUERY PLAN {compiledSql}";
                using var reader = explainCmd.ExecuteReader();
                while (reader.Read())
                {
                }
            }

            using (var probeCmd = readOnly.CreateCommand())
            {
                probeCmd.CommandText = $"SELECT * FROM ({compiledSql}) LIMIT 0";
                using var reader = probeCmd.ExecuteReader();
                var columns = Enumerable.Range(0, reader.FieldCount)
                    .Select(reader.GetName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!columns.Contains("instance_id") || !columns.Contains("torrent_hash"))
                {
                    return AdvancedSqlValidationResult.Failure("Query must return instance_id and torrent_hash columns.");
                }
            }
        }
        catch (SqliteException ex)
        {
            return AdvancedSqlValidationResult.Failure($"Invalid SQL: {ex.Message}");
        }

        return AdvancedSqlValidationResult.Ok(compiledSql);
    }

    public async Task<List<MatchedTorrent>> ExecuteAsync(SnapshotDatabase snapshot, string compiledSql, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var readOnly = OpenReadOnly(snapshot);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);

        using var command = readOnly.CreateCommand();
        command.CommandText = compiledSql;

        var results = new List<MatchedTorrent>();
        await using var reader = await command.ExecuteReaderAsync(cts.Token);
        var instanceIdOrdinal = reader.GetOrdinal("instance_id");
        var torrentHashOrdinal = reader.GetOrdinal("torrent_hash");

        while (results.Count < MaxRows && await reader.ReadAsync(cts.Token))
        {
            results.Add(new MatchedTorrent(reader.GetInt32(instanceIdOrdinal), reader.GetString(torrentHashOrdinal)));
        }

        return results;
    }

    private static string? ValidateShape(string rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
            return "SQL cannot be empty.";
        }

        var trimmed = rawSql.Trim().TrimEnd(';');
        if (trimmed.Contains(';'))
        {
            return "Only a single statement is allowed.";
        }

        var match = ForbiddenKeywordPattern.Match(trimmed);
        return match.Success ? $"'{match.Value.ToUpperInvariant()}' is not allowed in advanced conditions." : null;
    }

    /// <summary>Internal (not private) so tests can verify the SQLite-level read-only guarantee directly, independent of the regex-based shape check.</summary>
    internal static SqliteConnection OpenReadOnly(SnapshotDatabase snapshot) => snapshot.OpenReadOnlyConnection();
}
