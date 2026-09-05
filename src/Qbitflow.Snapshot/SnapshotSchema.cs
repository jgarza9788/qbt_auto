using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Qbitflow.Snapshot;

/// <summary>
/// DDL and user-defined functions for the in-memory snapshot database. Table/column
/// names here are the documented schema rule authors write conditions against (see the
/// Phase 7 field reference panel, which introspects this schema rather than hand-listing it).
/// </summary>
internal static class SnapshotSchema
{
    public const string Ddl = """
        CREATE TABLE torrents (
            instance_id INTEGER NOT NULL,
            instance_name TEXT NOT NULL,
            hash TEXT NOT NULL,
            name TEXT NOT NULL,
            category TEXT,
            tags TEXT,
            save_path TEXT,
            content_path TEXT,
            path_key TEXT,
            size_bytes INTEGER NOT NULL,
            progress REAL NOT NULL,
            state TEXT,
            downloaded_bytes INTEGER NOT NULL,
            uploaded_bytes INTEGER NOT NULL,
            ratio REAL NOT NULL,
            added_on TEXT,
            completion_on TEXT,
            upload_limit_bps INTEGER NOT NULL,
            download_limit_bps INTEGER NOT NULL,
            tracker TEXT,
            total_size_bytes INTEGER NOT NULL,
            amount_left_bytes INTEGER NOT NULL,
            completed_bytes INTEGER NOT NULL,
            dl_speed_bps INTEGER NOT NULL,
            up_speed_bps INTEGER NOT NULL,
            eta_seconds INTEGER NOT NULL,
            seeding_time_seconds INTEGER NOT NULL,
            active_time_seconds INTEGER NOT NULL,
            connected_seeds INTEGER NOT NULL,
            total_seeds INTEGER NOT NULL,
            connected_leechers INTEGER NOT NULL,
            total_leechers INTEGER NOT NULL,
            availability REAL NOT NULL,
            auto_tmm INTEGER NOT NULL,
            ratio_limit REAL NOT NULL,
            seeding_time_limit_minutes INTEGER NOT NULL,
            last_activity TEXT,
            seen_complete TEXT,
            PRIMARY KEY (instance_id, hash)
        );
        CREATE INDEX ix_torrents_path_key ON torrents(path_key);
        CREATE INDEX ix_torrents_category ON torrents(category);
        CREATE INDEX ix_torrents_state ON torrents(state);
        CREATE INDEX ix_torrents_tracker ON torrents(tracker);

        CREATE TABLE torrent_files (
            instance_id INTEGER NOT NULL,
            torrent_hash TEXT NOT NULL,
            file_path TEXT NOT NULL,
            path_key TEXT,
            size_bytes INTEGER NOT NULL,
            progress REAL NOT NULL
        );
        CREATE INDEX ix_torrent_files_hash ON torrent_files(instance_id, torrent_hash);
        CREATE INDEX ix_torrent_files_path_key ON torrent_files(path_key);

        CREATE TABLE media_items (
            instance_id INTEGER NOT NULL,
            instance_name TEXT NOT NULL,
            source_type TEXT NOT NULL,
            external_key TEXT NOT NULL,
            title TEXT NOT NULL,
            media_type TEXT,
            file_path TEXT,
            path_key TEXT,
            added_at TEXT
        );
        CREATE INDEX ix_media_items_path_key ON media_items(path_key);
        CREATE INDEX ix_media_items_external_key ON media_items(instance_id, external_key);

        CREATE TABLE watch_history (
            instance_id INTEGER NOT NULL,
            instance_name TEXT NOT NULL,
            media_title TEXT,
            file_path TEXT,
            path_key TEXT,
            user_name TEXT,
            watched_at TEXT,
            percent_complete REAL
        );
        CREATE INDEX ix_watch_history_path_key ON watch_history(path_key);
        CREATE INDEX ix_watch_history_watched_at ON watch_history(watched_at);

        CREATE VIEW play_counts AS
        SELECT
            COALESCE(path_key, 'title:' || IFNULL(media_title, '')) AS group_key,
            path_key,
            MAX(media_title) AS media_title,
            COUNT(*) AS play_count,
            COUNT(DISTINCT user_name) AS distinct_viewers,
            MIN(watched_at) AS first_watched_at,
            MAX(watched_at) AS last_watched_at
        FROM watch_history
        GROUP BY COALESCE(path_key, 'title:' || IFNULL(media_title, ''));

        CREATE TABLE storage_paths (
            storage_path_id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            path TEXT NOT NULL,
            available INTEGER NOT NULL,
            error TEXT,
            total_bytes INTEGER NOT NULL,
            used_bytes INTEGER NOT NULL,
            free_bytes INTEGER NOT NULL,
            used_percent REAL NOT NULL,
            free_percent REAL NOT NULL,
            folder_size_bytes INTEGER,
            folder_size_computed_at TEXT
        );
        """;

    public static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Ddl;
        command.ExecuteNonQuery();
    }

    public static void RegisterFunctions(SqliteConnection connection)
    {
        // days_since(iso8601_timestamp) -> days between now and that timestamp, or NULL.
        connection.CreateFunction<string?, double?>("days_since", isoTimestamp =>
        {
            if (string.IsNullOrEmpty(isoTimestamp))
            {
                return null;
            }
            return DateTimeOffset.TryParse(isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? (DateTimeOffset.UtcNow - dt).TotalDays
                : null;
        });

        // size_gb(bytes) -> bytes expressed in GB (decimal, 1e9), or NULL.
        connection.CreateFunction<long?, double?>("size_gb", bytes => bytes.HasValue ? bytes.Value / 1_000_000_000.0 : null);

        // path_matches(a, b) -> 1 if the two normalized path_keys are equal or one contains
        // the other (a torrent's content_path may be a parent folder of a media file's path).
        connection.CreateFunction<string?, string?, long>("path_matches", (a, b) =>
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return 0;
            }
            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return 1;
            }
            return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal) ? 1 : 0;
        });
    }
}
