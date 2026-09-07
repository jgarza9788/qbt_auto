using Microsoft.Data.Sqlite;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Engine.Conditions;
using Qbitflow.Engine.Conditions.AdvancedSql;
using Qbitflow.Snapshot;
using Xunit;

namespace Qbitflow.Tests.Engine;

public class AdvancedSqlExecutorTests : IDisposable
{
    private readonly SnapshotDatabase _db = new();
    private readonly AdvancedSqlExecutor _executor = new();

    private static readonly FieldResolutionContext Configured = FieldResolutionContext.For(
        [new InstanceRef(1, "qbt1", SourceType.Qbittorrent), new InstanceRef(2, "taut1", SourceType.Tautulli)],
        ["downloads"]);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Validate_RejectsEmptySql()
    {
        var result = _executor.Validate(_db, "   ", AdvancedSqlMode.WhereClause);
        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorMessage);
    }

    [Fact]
    public void Validate_RejectsMultipleStatements()
    {
        var result = _executor.Validate(_db, "1=1; DROP TABLE qbittorrent", AdvancedSqlMode.WhereClause);
        Assert.False(result.IsValid);
        Assert.Contains("single statement", result.ErrorMessage);
    }

    [Fact]
    public void Validate_RejectsForbiddenKeyword()
    {
        var result = _executor.Validate(_db, "1=1 OR (SELECT 1 WHERE 1 IN (DROP))", AdvancedSqlMode.WhereClause);
        Assert.False(result.IsValid);
        Assert.Contains("DROP", result.ErrorMessage);
    }

    [Fact]
    public void Validate_CatchesInvalidSql_ViaExplainQueryPlan()
    {
        var result = _executor.Validate(_db, "this_column_does_not_exist = 1", AdvancedSqlMode.WhereClause);
        Assert.False(result.IsValid);
        Assert.Contains("Invalid SQL", result.ErrorMessage);
    }

    [Fact]
    public void Validate_WhereClauseMode_AcceptsSimplePredicate()
    {
        var result = _executor.Validate(_db, "qbittorrent.*.category = 'linux'", AdvancedSqlMode.WhereClause);
        Assert.True(result.IsValid);
        Assert.Contains("FROM qbittorrent t WHERE (t.category) = 'linux'", result.CompiledSql);
    }

    [Fact]
    public void Validate_AcceptsSnapshotUdf_OnReadOnlyConnection()
    {
        // days_since / size_gb / path_matches are per-connection functions; advanced SQL must
        // see them on the hardened connection, not only the read-write one.
        var result = _executor.Validate(_db, "size_gb(qbittorrent.*.size_bytes) > 5", AdvancedSqlMode.WhereClause);
        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void Validate_ExpandsStorageField_IntoScalarSubquery()
    {
        var result = _executor.Validate(_db, "storage.downloads.used_percent > 85", AdvancedSqlMode.WhereClause);
        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Contains("(SELECT storage.used_percent FROM storage WHERE instance = 'downloads') > 85", result.CompiledSql);
    }

    [Fact]
    public void Validate_ExpandsComputedStorageField_UsingSizeGbUdf()
    {
        var result = _executor.Validate(_db, "storage.downloads.free_gb < 100", AdvancedSqlMode.WhereClause);
        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Contains("(SELECT size_gb(storage.free_bytes) FROM storage WHERE instance = 'downloads') < 100", result.CompiledSql);
    }

    [Fact]
    public void Validate_ExpandsComputedFieldKey_FromTheFieldReference()
    {
        var result = _executor.Validate(_db, "qbittorrent.*.active_days >= 14", AdvancedSqlMode.WhereClause);
        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Contains("(t.active_time_seconds / 86400.0) >= 14", result.CompiledSql);
    }

    [Fact]
    public void Validate_ExpandsComputedFieldKey_AlongsideAStorageField()
    {
        // The exact shape from the bug report: a storage field and a computed torrent key in
        // one predicate. active_days used to fall through to SQLite as "no such column".
        var result = _executor.Validate(_db, "storage.downloads.used_percent < 90 and qbittorrent.*.active_days >= 14", AdvancedSqlMode.WhereClause);
        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Contains("(SELECT storage.used_percent FROM storage WHERE instance = 'downloads') < 90", result.CompiledSql);
        Assert.Contains("(t.active_time_seconds / 86400.0) >= 14", result.CompiledSql);
    }

    [Fact]
    public void Validate_FullQueryMode_DoesNotExpandFieldKeys()
    {
        // FullQuery authors write real SQL against real columns and may not alias the table
        // "t", so keys needing the WHERE wrapper's alias are left alone and fail as real SQL.
        var result = _executor.Validate(_db, "SELECT instance_id, hash AS torrent_hash FROM qbittorrent WHERE qbittorrent.*.active_days >= 14", AdvancedSqlMode.FullQuery);
        Assert.False(result.IsValid);
        Assert.Contains("Invalid SQL", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_ComputedFieldKey_MatchesExpectedTorrents()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents =
            [
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "old", Name = "A", SizeBytes = 1, Progress = 1, ActiveTimeSeconds = 30 * 86400 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "new", Name = "B", SizeBytes = 1, Progress = 1, ActiveTimeSeconds = 3 * 86400 }
            ]
        });

        var validation = _executor.Validate(_db, "qbittorrent.*.active_days >= 14", AdvancedSqlMode.WhereClause);
        Assert.True(validation.IsValid, validation.ErrorMessage);

        var matches = await _executor.ExecuteAsync(_db, validation.CompiledSql!);

        var match = Assert.Single(matches);
        Assert.Equal("old", match.TorrentHash);
    }

    [Fact]
    public void Validate_RejectsUnknownStorageAttribute()
    {
        var result = _executor.Validate(_db, "storage.downloads.bogus > 1", AdvancedSqlMode.WhereClause);
        Assert.False(result.IsValid);
        Assert.Contains("Unknown field 'bogus'", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_StorageField_MatchesEveryTorrentWhenThresholdCrossed()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents =
            [
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "h1", Name = "A", SizeBytes = 1, Progress = 1 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "h2", Name = "B", SizeBytes = 1, Progress = 1 }
            ],
            StoragePaths =
            [
                new StorageUsageRecord { StoragePathId = 1, Name = "downloads", Path = "/downloads", Available = true, UsedPercent = 92.0, TotalBytes = 100, UsedBytes = 92, FreeBytes = 8 }
            ]
        });

        var validation = _executor.Validate(_db, "storage.downloads.used_percent > 85", AdvancedSqlMode.WhereClause);
        Assert.True(validation.IsValid, validation.ErrorMessage);

        var matches = await _executor.ExecuteAsync(_db, validation.CompiledSql!);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void Validate_FullQueryMode_RejectsQueryMissingRequiredColumns()
    {
        var result = _executor.Validate(_db, "SELECT hash FROM qbittorrent", AdvancedSqlMode.FullQuery);
        Assert.False(result.IsValid);
        Assert.Contains("instance_id", result.ErrorMessage);
        Assert.Contains("torrent_hash", result.ErrorMessage);
    }

    [Fact]
    public void Validate_FullQueryMode_AcceptsQueryWithRequiredColumns()
    {
        var result = _executor.Validate(_db, "SELECT instance_id, hash AS torrent_hash FROM qbittorrent WHERE category = 'linux'", AdvancedSqlMode.FullQuery);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsMatchingRows()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents =
            [
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "h1", Name = "A", Category = "linux", SizeBytes = 1, Progress = 1 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "h2", Name = "B", Category = "tv", SizeBytes = 1, Progress = 1 }
            ]
        });

        var validation = _executor.Validate(_db, "qbittorrent.*.category = 'linux'", AdvancedSqlMode.WhereClause);
        Assert.True(validation.IsValid);

        var matches = await _executor.ExecuteAsync(_db, validation.CompiledSql!);

        var match = Assert.Single(matches);
        Assert.Equal("h1", match.TorrentHash);
    }

    [Fact]
    public void OpenReadOnly_ActuallyRejectsWrites_AtTheSqliteLevel()
    {
        using var readOnly = AdvancedSqlExecutor.OpenReadOnly(_db);
        using var command = readOnly.CreateCommand();
        command.CommandText = "DELETE FROM qbittorrent";

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void OpenReadOnly_StillAllowsReadsAgainstTheSameSnapshot()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents = [new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "h1", Name = "A", SizeBytes = 1, Progress = 1 }]
        });

        using var readOnly = AdvancedSqlExecutor.OpenReadOnly(_db);
        using var command = readOnly.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM qbittorrent";

        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Validate_ExpandsAggregateKey_IntoACorrelatedSubquery()
    {
        var result = _executor.Validate(_db, "tautulli.*.play_count = 0", AdvancedSqlMode.WhereClause, Configured);

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Contains("(SELECT COUNT(*) FROM tautulli x0 WHERE x0.path_key = t.path_key AND x0.kind = 'history') = 0",
            result.CompiledSql);
    }

    [Fact]
    public void Validate_NamedTorrentInstance_YieldsNullOnOtherInstances()
    {
        var result = _executor.Validate(_db, "qbittorrent.qbt1.ratio > 1", AdvancedSqlMode.WhereClause, Configured);

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Contains("(CASE WHEN t.instance = 'qbt1' THEN t.ratio END) > 1", result.CompiledSql);
    }

    [Fact]
    public void Validate_RejectsPerRowFieldOfARelatedSource()
    {
        // A related source has many rows per torrent, so its row fields have no scalar value in
        // hand-written SQL. The error has to say that rather than let SQLite say "no such column".
        var result = _executor.Validate(_db, "tautulli.*.watched_at > '2026-01-01'", AdvancedSqlMode.WhereClause, Configured);

        Assert.False(result.IsValid);
        Assert.Contains("per-row field", result.ErrorMessage);
        Assert.Contains("play_count", result.ErrorMessage);
    }

    [Fact]
    public void Validate_RejectsUnknownInstance_NamingTheConfiguredOnes()
    {
        var result = _executor.Validate(_db, "tautulli.nope.play_count = 0", AdvancedSqlMode.WhereClause, Configured);

        Assert.False(result.IsValid);
        Assert.Contains("taut1", result.ErrorMessage);
    }

    [Fact]
    public void Validate_LeavesAliasQualifiedColumnsAlone()
    {
        // "t.category" has a dot but isn't a field key, so the author's own qualified column
        // must survive untouched.
        var result = _executor.Validate(_db, "t.category = 'linux'", AdvancedSqlMode.WhereClause, Configured);

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Contains("WHERE t.category = 'linux'", result.CompiledSql);
    }

    [Fact]
    public void Validate_LeavesThreeSegmentNamesAloneUnlessTheyStartWithASourceType()
    {
        // main.qbittorrent.ratio is schema.table.column, not a field key. Proof that it is left
        // alone is that SQLite -- not the expander -- is what rejects it.
        var result = _executor.Validate(_db, "main.qbittorrent.ratio > 1", AdvancedSqlMode.WhereClause, Configured);

        Assert.False(result.IsValid);
        Assert.Contains("no such column: main.qbittorrent.ratio", result.ErrorMessage);
    }

    [Fact]
    public void Validate_IgnoresKeysInsideStringLiteralsAndComments()
    {
        var result = _executor.Validate(
            _db,
            "/* qbittorrent.*.ratio */ qbittorrent.*.name = 'qbittorrent.*.ratio'",
            AdvancedSqlMode.WhereClause,
            Configured);

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Contains("/* qbittorrent.*.ratio */ (t.name) = 'qbittorrent.*.ratio'", result.CompiledSql);
        Assert.DoesNotContain("(t.ratio)", result.CompiledSql);
    }

    [Fact]
    public async Task ExecuteAsync_AggregateKey_FindsNeverWatchedTorrents()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents =
            [
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt1", Hash = "watched", Name = "A", ContentPath = "/media/a.mkv", SizeBytes = 1, Progress = 1 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt1", Hash = "never", Name = "B", ContentPath = "/media/b.mkv", SizeBytes = 1, Progress = 1 }
            ],
            WatchHistory =
            [
                new WatchHistoryRecord
                {
                    InstanceId = 2, InstanceName = "taut1", SourceType = SourceType.Tautulli,
                    FilePath = "/media/a.mkv", UserName = "alice", WatchedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        var validation = _executor.Validate(_db, "tautulli.*.play_count = 0", AdvancedSqlMode.WhereClause, Configured);
        Assert.True(validation.IsValid, validation.ErrorMessage);

        var matches = await _executor.ExecuteAsync(_db, validation.CompiledSql!);

        Assert.Equal("never", Assert.Single(matches).TorrentHash);
    }
}
