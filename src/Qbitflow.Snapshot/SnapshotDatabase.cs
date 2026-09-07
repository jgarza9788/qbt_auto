using Microsoft.Data.Sqlite;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Snapshot;

/// <summary>
/// One in-memory SQLite database representing "the world as of the last refresh cycle".
/// All rules in a cycle evaluate against the same Rebuild() call's output, so results are
/// consistent for the whole cycle. Rebuild always does a full delete+insert per table --
/// every refresh re-fetches the complete current state from each enabled instance (the
/// source-level cache is what avoids over-fetching, see Qbitflow.Sources), so there is
/// never a partial delta to reconcile here.
/// </summary>
public class SnapshotDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public SnapshotDatabase()
    {
        DataSourceName = $"file:qbitflow-snapshot-{Guid.NewGuid():N}";
        _connection = new SqliteConnection($"Data Source={DataSourceName};Mode=Memory;Cache=Shared");
        _connection.Open();
        SnapshotSchema.RegisterFunctions(_connection);
        SnapshotSchema.CreateSchema(_connection);
    }

    /// <summary>The live (read-write) connection standard-mode compiled queries execute against.</summary>
    public SqliteConnection Connection => _connection;

    /// <summary>The shared-cache data source name of the underlying in-memory database. Prefer <see cref="OpenReadOnlyConnection"/> over opening a raw connection to this yourself.</summary>
    public string DataSourceName { get; }

    /// <summary>
    /// Opens a second, hardened connection to the same in-memory database for advanced/raw-SQL
    /// mode: <c>PRAGMA query_only</c> makes SQLite itself reject any write, and the snapshot's
    /// user-defined functions (days_since, size_gb, path_matches) are registered here too --
    /// they're per-connection, so an advanced query must have them available on this exact
    /// connection, not just the read-write one. Caller owns disposal.
    /// </summary>
    public SqliteConnection OpenReadOnlyConnection()
    {
        // Mode=Memory (not Mode=ReadOnly) is required -- a shared-cache in-memory database is
        // only reachable via mode=memory, which SQLite's URI parser can't combine with
        // mode=ro. query_only below is what actually makes this read-only.
        var connection = new SqliteConnection($"Data Source={DataSourceName};Mode=Memory;Cache=Shared");
        connection.Open();

        SnapshotSchema.RegisterFunctions(connection);

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA query_only = ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    public void Rebuild(SnapshotInput input)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            RebuildQbittorrent(transaction, input.Torrents, input.PathMappingRules);
            RebuildQbittorrentFiles(transaction, input.TorrentFiles, input.PathMappingRules);
            RebuildMediaHistory(transaction, input.MediaItems, input.WatchHistory, input.PathMappingRules);
            RebuildStorage(transaction, input.StoragePaths);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void RebuildQbittorrent(SqliteTransaction tx, IEnumerable<TorrentRecord> torrents, IReadOnlyList<PathMappingRule> rules)
    {
        Execute(tx, "DELETE FROM qbittorrent");

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO qbittorrent
            (instance_id, instance, hash, name, category, tags, save_path, content_path, path_key,
             size_bytes, progress, state, downloaded_bytes, uploaded_bytes, ratio, added_on, completion_on,
             upload_limit_bps, download_limit_bps, tracker, total_size_bytes, amount_left_bytes, completed_bytes,
             dl_speed_bps, up_speed_bps, eta_seconds, seeding_time_seconds, active_time_seconds,
             connected_seeds, total_seeds, connected_leechers, total_leechers, availability, auto_tmm,
             ratio_limit, seeding_time_limit_minutes, last_activity, seen_complete)
            VALUES
            ($instance_id, $instance, $hash, $name, $category, $tags, $save_path, $content_path, $path_key,
             $size_bytes, $progress, $state, $downloaded_bytes, $uploaded_bytes, $ratio, $added_on, $completion_on,
             $upload_limit_bps, $download_limit_bps, $tracker, $total_size_bytes, $amount_left_bytes, $completed_bytes,
             $dl_speed_bps, $up_speed_bps, $eta_seconds, $seeding_time_seconds, $active_time_seconds,
             $connected_seeds, $total_seeds, $connected_leechers, $total_leechers, $availability, $auto_tmm,
             $ratio_limit, $seeding_time_limit_minutes, $last_activity, $seen_complete)
            """;

        foreach (var t in torrents)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$instance_id", t.InstanceId);
            insert.Parameters.AddWithValue("$instance", t.InstanceName);
            insert.Parameters.AddWithValue("$hash", t.Hash);
            insert.Parameters.AddWithValue("$name", t.Name);
            insert.Parameters.AddWithValue("$category", DbValues.Of(t.Category));
            insert.Parameters.AddWithValue("$tags", t.Tags.Count > 0 ? string.Join(",", t.Tags) : (object)DBNull.Value);
            insert.Parameters.AddWithValue("$save_path", DbValues.Of(t.SavePath));
            insert.Parameters.AddWithValue("$content_path", DbValues.Of(t.ContentPath));
            insert.Parameters.AddWithValue("$path_key", DbValues.Of(PathKeyNormalizer.Normalize(t.ContentPath ?? t.SavePath, rules)));
            insert.Parameters.AddWithValue("$size_bytes", t.SizeBytes);
            insert.Parameters.AddWithValue("$progress", t.Progress);
            insert.Parameters.AddWithValue("$state", DbValues.Of(t.State));
            insert.Parameters.AddWithValue("$downloaded_bytes", t.DownloadedBytes);
            insert.Parameters.AddWithValue("$uploaded_bytes", t.UploadedBytes);
            insert.Parameters.AddWithValue("$ratio", t.Ratio);
            insert.Parameters.AddWithValue("$added_on", DbValues.Of(t.AddedOn));
            insert.Parameters.AddWithValue("$completion_on", DbValues.Of(t.CompletionOn));
            insert.Parameters.AddWithValue("$upload_limit_bps", t.UploadLimitBytesPerSec);
            insert.Parameters.AddWithValue("$download_limit_bps", t.DownloadLimitBytesPerSec);
            insert.Parameters.AddWithValue("$tracker", DbValues.Of(t.Tracker));
            insert.Parameters.AddWithValue("$total_size_bytes", t.TotalSizeBytes);
            insert.Parameters.AddWithValue("$amount_left_bytes", t.AmountLeftBytes);
            insert.Parameters.AddWithValue("$completed_bytes", t.CompletedBytes);
            insert.Parameters.AddWithValue("$dl_speed_bps", t.DownloadSpeedBytesPerSec);
            insert.Parameters.AddWithValue("$up_speed_bps", t.UploadSpeedBytesPerSec);
            insert.Parameters.AddWithValue("$eta_seconds", t.EtaSeconds);
            insert.Parameters.AddWithValue("$seeding_time_seconds", t.SeedingTimeSeconds);
            insert.Parameters.AddWithValue("$active_time_seconds", t.ActiveTimeSeconds);
            insert.Parameters.AddWithValue("$connected_seeds", t.ConnectedSeeds);
            insert.Parameters.AddWithValue("$total_seeds", t.TotalSeeds);
            insert.Parameters.AddWithValue("$connected_leechers", t.ConnectedLeechers);
            insert.Parameters.AddWithValue("$total_leechers", t.TotalLeechers);
            insert.Parameters.AddWithValue("$availability", t.Availability);
            insert.Parameters.AddWithValue("$auto_tmm", t.AutoTmmEnabled ? 1 : 0);
            insert.Parameters.AddWithValue("$ratio_limit", t.RatioLimit);
            insert.Parameters.AddWithValue("$seeding_time_limit_minutes", t.SeedingTimeLimitMinutes);
            insert.Parameters.AddWithValue("$last_activity", DbValues.Of(t.LastActivityOn));
            insert.Parameters.AddWithValue("$seen_complete", DbValues.Of(t.SeenCompleteOn));
            insert.ExecuteNonQuery();
        }
    }

    private void RebuildQbittorrentFiles(SqliteTransaction tx, IEnumerable<TorrentFileRecord> files, IReadOnlyList<PathMappingRule> rules)
    {
        Execute(tx, "DELETE FROM qbittorrent_files");

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO qbittorrent_files (instance_id, torrent_hash, file_path, path_key, size_bytes, progress)
            VALUES ($instance_id, $torrent_hash, $file_path, $path_key, $size_bytes, $progress)
            """;

        foreach (var f in files)
        {
            insert.Parameters.Clear();
            // TorrentFileRecord doesn't carry an instance id -- it's always fetched in the
            // context of one specific instance's torrent, so callers key it separately;
            // 0 here just means "not tracked at this granularity" until Phase 5/6 need it.
            insert.Parameters.AddWithValue("$instance_id", 0);
            insert.Parameters.AddWithValue("$torrent_hash", f.TorrentHash);
            insert.Parameters.AddWithValue("$file_path", f.FilePath);
            insert.Parameters.AddWithValue("$path_key", DbValues.Of(PathKeyNormalizer.Normalize(f.FilePath, rules)));
            insert.Parameters.AddWithValue("$size_bytes", f.SizeBytes);
            insert.Parameters.AddWithValue("$progress", f.Progress);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Writes media-library items and watch events into the table named after the source type
    /// that produced them, discriminated by the "kind" column. Every one of those tables shares
    /// the same wide shape, so a single insert statement is reused across all of them -- the only
    /// thing that varies is the table name, which comes from the enum and never from user input.
    /// Tables belonging to types with no configured instance are simply left empty.
    /// </summary>
    private void RebuildMediaHistory(
        SqliteTransaction tx,
        IEnumerable<MediaItemRecord> mediaItems,
        IEnumerable<WatchHistoryRecord> watchHistory,
        IReadOnlyList<PathMappingRule> rules)
    {
        foreach (var type in SourceNaming.MediaHistoryTypes)
        {
            Execute(tx, $"DELETE FROM {SnapshotSchema.TableFor(type)}");
        }

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;

        foreach (var m in mediaItems)
        {
            // One row per file path -- almost always exactly one, but a multi-version Plex
            // item can have more than one, and an item with none still gets a single row
            // (file_path/path_key NULL) so it's visible even without a resolvable file.
            var filePaths = m.FilePaths.Count > 0 ? m.FilePaths : [null];
            foreach (var filePath in filePaths)
            {
                insert.CommandText = InsertInto(m.SourceType);
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$instance_id", m.InstanceId);
                insert.Parameters.AddWithValue("$instance", m.InstanceName);
                insert.Parameters.AddWithValue("$kind", SnapshotSchema.KindMedia);
                insert.Parameters.AddWithValue("$external_key", m.ExternalKey);
                insert.Parameters.AddWithValue("$title", m.Title);
                insert.Parameters.AddWithValue("$media_type", DbValues.Of(m.MediaType));
                insert.Parameters.AddWithValue("$file_path", DbValues.Of(filePath));
                insert.Parameters.AddWithValue("$path_key", DbValues.Of(PathKeyNormalizer.Normalize(filePath, rules)));
                insert.Parameters.AddWithValue("$added_at", DbValues.Of(m.AddedAt));
                insert.Parameters.AddWithValue("$user_name", DBNull.Value);
                insert.Parameters.AddWithValue("$watched_at", DBNull.Value);
                insert.Parameters.AddWithValue("$percent_complete", DBNull.Value);
                insert.ExecuteNonQuery();
            }
        }

        foreach (var w in watchHistory)
        {
            insert.CommandText = InsertInto(w.SourceType);
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$instance_id", w.InstanceId);
            insert.Parameters.AddWithValue("$instance", w.InstanceName);
            insert.Parameters.AddWithValue("$kind", SnapshotSchema.KindHistory);
            insert.Parameters.AddWithValue("$external_key", DBNull.Value);
            insert.Parameters.AddWithValue("$title", DbValues.Of(w.MediaTitle));
            insert.Parameters.AddWithValue("$media_type", DBNull.Value);
            insert.Parameters.AddWithValue("$file_path", DbValues.Of(w.FilePath));
            insert.Parameters.AddWithValue("$path_key", DbValues.Of(PathKeyNormalizer.Normalize(w.FilePath, rules)));
            insert.Parameters.AddWithValue("$added_at", DBNull.Value);
            insert.Parameters.AddWithValue("$user_name", DbValues.Of(w.UserName));
            insert.Parameters.AddWithValue("$watched_at", DbValues.Of(w.WatchedAt));
            insert.Parameters.AddWithValue("$percent_complete", DbValues.Of(w.PercentComplete));
            insert.ExecuteNonQuery();
        }
    }

    private static string InsertInto(SourceType type)
    {
        if (type == SourceNaming.AnchorType)
        {
            throw new InvalidOperationException(
                $"{type} records belong in the qbittorrent table, not the shared media/history shape.");
        }

        return $"""
            INSERT INTO {SnapshotSchema.TableFor(type)}
            (instance_id, instance, kind, external_key, title, media_type, file_path, path_key,
             added_at, user_name, watched_at, percent_complete)
            VALUES
            ($instance_id, $instance, $kind, $external_key, $title, $media_type, $file_path, $path_key,
             $added_at, $user_name, $watched_at, $percent_complete)
            """;
    }

    private void RebuildStorage(SqliteTransaction tx, IEnumerable<StorageUsageRecord> paths)
    {
        Execute(tx, "DELETE FROM storage");

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO storage
            (storage_path_id, instance, path, available, error, total_bytes, used_bytes, free_bytes,
             used_percent, free_percent, folder_size_bytes, folder_size_computed_at)
            VALUES
            ($storage_path_id, $instance, $path, $available, $error, $total_bytes, $used_bytes, $free_bytes,
             $used_percent, $free_percent, $folder_size_bytes, $folder_size_computed_at)
            """;

        foreach (var s in paths)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$storage_path_id", s.StoragePathId);
            insert.Parameters.AddWithValue("$instance", s.Name);
            insert.Parameters.AddWithValue("$path", s.Path);
            insert.Parameters.AddWithValue("$available", s.Available ? 1 : 0);
            insert.Parameters.AddWithValue("$error", DbValues.Of(s.Error));
            insert.Parameters.AddWithValue("$total_bytes", s.TotalBytes);
            insert.Parameters.AddWithValue("$used_bytes", s.UsedBytes);
            insert.Parameters.AddWithValue("$free_bytes", s.FreeBytes);
            insert.Parameters.AddWithValue("$used_percent", s.UsedPercent);
            insert.Parameters.AddWithValue("$free_percent", s.FreePercent);
            insert.Parameters.AddWithValue("$folder_size_bytes", DbValues.Of(s.FolderSizeBytes));
            insert.Parameters.AddWithValue("$folder_size_computed_at", DbValues.Of(s.FolderSizeComputedAt));
            insert.ExecuteNonQuery();
        }
    }

    private void Execute(SqliteTransaction tx, string sql)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
