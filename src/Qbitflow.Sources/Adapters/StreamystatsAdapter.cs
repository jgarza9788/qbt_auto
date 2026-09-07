using System.Diagnostics;
using Npgsql;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Core.Interfaces;

namespace Qbitflow.Sources.Adapters;

/// <summary>
/// Reads playback history straight out of the Streamystats PostgreSQL database.
///
/// This is the only adapter that isn't HTTP, and that is forced rather than chosen. Streamystats'
/// REST API cannot serve this data: <c>/api/export/&lt;serverId&gt;</c> is the only endpoint holding
/// playback sessions and it is gated behind a browser session cookie (requireAdmin -> requireSession
/// -> a JWT cookie), with login implemented as a Next.js server action rather than a callable
/// endpoint. Every API-key-reachable route returns something else entirely.
///
/// The database is also the only place the <em>file path</em> exists. A Streamystats session row
/// records what was played but not where the file is; the path lives on the separate items table,
/// populated from Jellyfin's Path. qbitflow correlates a playback event to a torrent by normalized
/// file path, so without that join the rows would import and then never match anything.
///
/// The connection is opened read-only (default_transaction_read_only) and only ever SELECTs.
/// </summary>
public class StreamystatsAdapter : ISourceAdapter
{
    public SourceType SourceType => SourceType.Streamystats;

    /// <summary>
    /// One row per playback session, with the file path resolved through the item it played.
    /// LEFT JOIN rather than INNER so a session whose item has since been removed from the library
    /// still imports -- it just can't correlate to a torrent, same as any path-less row.
    /// </summary>
    private const string HistorySql = """
        SELECT s.item_name,
               s.user_name,
               s.start_time,
               s.percent_complete,
               i.path
        FROM sessions s
        LEFT JOIN items i ON i.id = s.item_id AND i.server_id = s.server_id
        WHERE (@server_id IS NULL OR s.server_id = @server_id)
          AND s.start_time IS NOT NULL
        ORDER BY s.start_time DESC
        LIMIT @max_rows
        """;

    public async Task<SourceFetchResult> FetchAsync(SourceConnectionInfo connection, CancellationToken ct = default)
    {
        var config = StreamystatsConnectionConfig.Parse(connection);

        await using var db = new NpgsqlConnection(config.ConnectionString);
        await db.OpenAsync(ct);

        await using var command = new NpgsqlCommand(HistorySql, db);
        command.Parameters.AddWithValue("server_id", (object?)config.ServerId ?? DBNull.Value);
        command.Parameters.AddWithValue("max_rows", config.MaxRows);

        var records = new List<WatchHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new WatchHistoryRecord
            {
                InstanceId = connection.InstanceId,
                InstanceName = connection.InstanceName,
                SourceType = SourceType,
                MediaTitle = reader.IsDBNull(0) ? null : reader.GetString(0),
                UserName = reader.IsDBNull(1) ? null : reader.GetString(1),
                WatchedAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                PercentComplete = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                FilePath = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }

        return new SourceFetchResult { WatchHistory = records };
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(SourceConnectionInfo connection, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var config = StreamystatsConnectionConfig.Parse(connection);

            await using var db = new NpgsqlConnection(config.ConnectionString);
            await db.OpenAsync(ct);

            // Count what will actually be imported, and how much of it can correlate to a torrent.
            // A path-less count is the difference between "connected" and "connected and useful",
            // and it is the single most common thing to get wrong here.
            await using var command = new NpgsqlCommand("""
                SELECT COUNT(*), COUNT(i.path)
                FROM sessions s
                LEFT JOIN items i ON i.id = s.item_id AND i.server_id = s.server_id
                WHERE (@server_id IS NULL OR s.server_id = @server_id)
                """, db);
            command.Parameters.AddWithValue("server_id", (object?)config.ServerId ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return Failure("Connected, but the sessions query returned nothing at all.", sw);
            }

            var total = reader.GetInt64(0);
            var withPath = reader.GetInt64(1);

            var message = $"Connected ({total} playback session(s), {withPath} with a file path).";
            if (total > 0 && withPath == 0)
            {
                message += " None have a path, so none can be matched to a torrent -- has Streamystats"
                         + " finished its library sync?";
            }
            if (config.BaseUrlHadCredentials)
            {
                message += " Note: credentials in the base URL were ignored; use the Username/Password fields.";
            }

            return new ConnectionTestResult { Success = true, Message = message, Duration = sw.Elapsed };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return Failure(
                $"Connected to the database, but it has no '{ex.TableName ?? "sessions"}' table -- "
                + "is this the Streamystats database? Set a different one with {\"database\": \"...\"} in Extra config.",
                sw);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return Failure($"No such database: {ex.MessageText}", sw);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidPassword
                                        || ex.SqlState == PostgresErrorCodes.InvalidAuthorizationSpecification)
        {
            return Failure("PostgreSQL rejected the username/password.", sw);
        }
        catch (Exception ex)
        {
            return Failure(ex.Message, sw);
        }
    }

    private static ConnectionTestResult Failure(string message, Stopwatch sw) =>
        new() { Success = false, Message = message, Duration = sw.Elapsed };
}
