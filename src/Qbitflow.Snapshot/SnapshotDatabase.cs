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

    /// <summary>The shared-cache data source name -- open a second connection with Mode=ReadOnly against this same string to get a hardened connection to the same in-memory database (used by advanced/raw-SQL mode).</summary>
    public string DataSourceName { get; }

    public void Rebuild(SnapshotInput input)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            RebuildTorrents(transaction, input.Torrents, input.PathMappingRules);
            RebuildTorrentFiles(transaction, input.TorrentFiles, input.PathMappingRules);
            RebuildMediaItems(transaction, input.MediaItems, input.PathMappingRules);
            RebuildWatchHistory(transaction, input.WatchHistory, input.PathMappingRules);
            RebuildStoragePaths(transaction, input.StoragePaths);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void RebuildTorrents(SqliteTransaction tx, IEnumerable<TorrentRecord> torrents, IReadOnlyList<PathMappingRule> rules)
    {
        Execute(tx, "DELETE FROM torrents");

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO torrents
            (instance_id, instance_name, hash, name, category, tags, save_path, content_path, path_key,
             size_bytes, progress, state, downloaded_bytes, uploaded_bytes, ratio, added_on, completion_on,
             upload_limit_bps, download_limit_bps)
            VALUES
            ($instance_id, $instance_name, $hash, $name, $category, $tags, $save_path, $content_path, $path_key,
             $size_bytes, $progress, $state, $downloaded_bytes, $uploaded_bytes, $ratio, $added_on, $completion_on,
             $upload_limit_bps, $download_limit_bps)
            """;

        foreach (var t in torrents)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$instance_id", t.InstanceId);
            insert.Parameters.AddWithValue("$instance_name", t.InstanceName);
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
            insert.ExecuteNonQuery();
        }
    }

    private void RebuildTorrentFiles(SqliteTransaction tx, IEnumerable<TorrentFileRecord> files, IReadOnlyList<PathMappingRule> rules)
    {
        Execute(tx, "DELETE FROM torrent_files");

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO torrent_files (instance_id, torrent_hash, file_path, path_key, size_bytes, progress)
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

    private void RebuildMediaItems(SqliteTransaction tx, IEnumerable<MediaItemRecord> items, IReadOnlyList<PathMappingRule> rules)
    {
        Execute(tx, "DELETE FROM media_items");

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO media_items
            (instance_id, instance_name, source_type, external_key, title, media_type, file_path, path_key, added_at)
            VALUES
            ($instance_id, $instance_name, $source_type, $external_key, $title, $media_type, $file_path, $path_key, $added_at)
            """;

        foreach (var m in items)
        {
            // One row per file path -- almost always exactly one, but a multi-version Plex
            // item can have more than one, and an item with none still gets a single row
            // (file_path/path_key NULL) so it's visible even without a resolvable file.
            var filePaths = m.FilePaths.Count > 0 ? m.FilePaths : [null];
            foreach (var filePath in filePaths)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$instance_id", m.InstanceId);
                insert.Parameters.AddWithValue("$instance_name", m.InstanceName);
                insert.Parameters.AddWithValue("$source_type", m.SourceType.ToString());
                insert.Parameters.AddWithValue("$external_key", m.ExternalKey);
                insert.Parameters.AddWithValue("$title", m.Title);
                insert.Parameters.AddWithValue("$media_type", DbValues.Of(m.MediaType));
                insert.Parameters.AddWithValue("$file_path", DbValues.Of(filePath));
                insert.Parameters.AddWithValue("$path_key", DbValues.Of(PathKeyNormalizer.Normalize(filePath, rules)));
                insert.Parameters.AddWithValue("$added_at", DbValues.Of(m.AddedAt));
                insert.ExecuteNonQuery();
            }
        }
    }

    private void RebuildWatchHistory(SqliteTransaction tx, IEnumerable<WatchHistoryRecord> history, IReadOnlyList<PathMappingRule> rules)
    {
        Execute(tx, "DELETE FROM watch_history");

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO watch_history
            (instance_id, instance_name, media_title, file_path, path_key, user_name, watched_at, percent_complete)
            VALUES
            ($instance_id, $instance_name, $media_title, $file_path, $path_key, $user_name, $watched_at, $percent_complete)
            """;

        foreach (var w in history)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$instance_id", w.InstanceId);
            insert.Parameters.AddWithValue("$instance_name", w.InstanceName);
            insert.Parameters.AddWithValue("$media_title", DbValues.Of(w.MediaTitle));
            insert.Parameters.AddWithValue("$file_path", DbValues.Of(w.FilePath));
            insert.Parameters.AddWithValue("$path_key", DbValues.Of(PathKeyNormalizer.Normalize(w.FilePath, rules)));
            insert.Parameters.AddWithValue("$user_name", DbValues.Of(w.UserName));
            insert.Parameters.AddWithValue("$watched_at", DbValues.Of(w.WatchedAt));
            insert.Parameters.AddWithValue("$percent_complete", DbValues.Of(w.PercentComplete));
            insert.ExecuteNonQuery();
        }
    }

    private void RebuildStoragePaths(SqliteTransaction tx, IEnumerable<StorageUsageRecord> paths)
    {
        Execute(tx, "DELETE FROM storage_paths");

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO storage_paths
            (storage_path_id, name, path, available, error, total_bytes, used_bytes, free_bytes,
             used_percent, free_percent, folder_size_bytes, folder_size_computed_at)
            VALUES
            ($storage_path_id, $name, $path, $available, $error, $total_bytes, $used_bytes, $free_bytes,
             $used_percent, $free_percent, $folder_size_bytes, $folder_size_computed_at)
            """;

        foreach (var s in paths)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$storage_path_id", s.StoragePathId);
            insert.Parameters.AddWithValue("$name", s.Name);
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
