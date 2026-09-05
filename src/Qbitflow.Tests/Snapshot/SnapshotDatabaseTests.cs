using Microsoft.Data.Sqlite;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Snapshot;
using Xunit;

namespace Qbitflow.Tests.Snapshot;

public class SnapshotDatabaseTests : IDisposable
{
    private readonly SnapshotDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Rebuild_InsertsTorrents_WithComputedPathKey()
    {
        var input = new SnapshotInput
        {
            Torrents =
            [
                new TorrentRecord
                {
                    InstanceId = 1, InstanceName = "Main", Hash = "h1", Name = "Ubuntu ISO",
                    Category = "linux", Tags = ["iso", "verified"],
                    ContentPath = "/downloads/ubuntu/ubuntu.iso", SizeBytes = 5_000_000_000,
                    Progress = 1.0, State = "uploading", DownloadedBytes = 5_000_000_000,
                    UploadedBytes = 1_000_000_000, Ratio = 0.2,
                    AddedOn = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                }
            ]
        };

        _db.Rebuild(input);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT hash, name, category, tags, path_key, size_bytes FROM torrents";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("h1", reader.GetString(0));
        Assert.Equal("Ubuntu ISO", reader.GetString(1));
        Assert.Equal("linux", reader.GetString(2));
        Assert.Equal("iso,verified", reader.GetString(3));
        Assert.Equal("/downloads/ubuntu/ubuntu.iso", reader.GetString(4));
        Assert.Equal(5_000_000_000, reader.GetInt64(5));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Rebuild_JoinsTorrentsToMediaItems_ViaPathKey_AfterPathMapping()
    {
        var rules = new List<PathMappingRule>
        {
            new() { SourcePrefix = "/downloads", CanonicalPrefix = "/media" }
        };

        var input = new SnapshotInput
        {
            PathMappingRules = rules,
            Torrents =
            [
                new TorrentRecord
                {
                    InstanceId = 1, InstanceName = "qbt", Hash = "h1", Name = "Foo.2020",
                    ContentPath = "/downloads/Foo.2020/Foo.mkv", SizeBytes = 1000, Progress = 1
                }
            ],
            MediaItems =
            [
                new MediaItemRecord
                {
                    InstanceId = 2, InstanceName = "plex", SourceType = SourceType.Plex,
                    ExternalKey = "123", Title = "Foo", MediaType = "movie",
                    FilePaths = ["/media/Foo.2020/Foo.mkv"]
                }
            ]
        };

        _db.Rebuild(input);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT t.hash, m.title
            FROM torrents t
            JOIN media_items m ON t.path_key = m.path_key
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("h1", reader.GetString(0));
        Assert.Equal("Foo", reader.GetString(1));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Rebuild_ReplacesPreviousData_OnSecondCall()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents = [new TorrentRecord { InstanceId = 1, InstanceName = "a", Hash = "old", Name = "Old", SizeBytes = 1, Progress = 1 }]
        });

        _db.Rebuild(new SnapshotInput
        {
            Torrents = [new TorrentRecord { InstanceId = 1, InstanceName = "a", Hash = "new", Name = "New", SizeBytes = 1, Progress = 1 }]
        });

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT hash FROM torrents";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("new", reader.GetString(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Rebuild_StoragePaths_IncludesUnavailableRowsWithError()
    {
        _db.Rebuild(new SnapshotInput
        {
            StoragePaths =
            [
                new StorageUsageRecord { StoragePathId = 1, Name = "ghost", Path = "/nope", Available = false, Error = "Path does not exist or is not mounted." }
            ]
        });

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT available, error FROM storage_paths WHERE storage_path_id = 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal("Path does not exist or is not mounted.", reader.GetString(1));
    }

    [Fact]
    public void PlayCounts_View_AggregatesWatchHistoryByPathKey()
    {
        _db.Rebuild(new SnapshotInput
        {
            WatchHistory =
            [
                new WatchHistoryRecord { InstanceId = 1, InstanceName = "t", FilePath = "/media/foo.mkv", UserName = "alice", WatchedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), PercentComplete = 100 },
                new WatchHistoryRecord { InstanceId = 1, InstanceName = "t", FilePath = "/media/foo.mkv", UserName = "bob", WatchedAt = DateTimeOffset.Parse("2026-01-05T00:00:00Z"), PercentComplete = 95 },
                new WatchHistoryRecord { InstanceId = 1, InstanceName = "t", FilePath = "/media/bar.mkv", UserName = "alice", WatchedAt = DateTimeOffset.Parse("2026-01-02T00:00:00Z"), PercentComplete = 100 }
            ]
        });

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT play_count, distinct_viewers FROM play_counts WHERE path_key = '/media/foo.mkv'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
    }

    [Fact]
    public void Udf_DaysSince_ComputesDaysBetweenNowAndTimestamp()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT days_since($ts)";
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.AddDays(-10).ToString("o"));
        var result = (double)cmd.ExecuteScalar()!;

        Assert.InRange(result, 9.9, 10.1);
    }

    [Fact]
    public void Udf_DaysSince_ReturnsNull_ForNullInput()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT days_since(NULL)";
        var result = cmd.ExecuteScalar();

        Assert.Equal(DBNull.Value, result);
    }

    [Fact]
    public void Udf_SizeGb_ConvertsBytesToGigabytes()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT size_gb(5000000000)";
        var result = (double)cmd.ExecuteScalar()!;

        Assert.Equal(5.0, result, precision: 6);
    }

    [Theory]
    [InlineData("/media/foo", "/media/foo", 1)]
    [InlineData("/media/foo/bar.mkv", "/media/foo", 1)]
    [InlineData("/media/foo", "/media/bar", 0)]
    public void Udf_PathMatches_ComparesNormalizedKeys(string a, string b, long expected)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT path_matches($a, $b)";
        cmd.Parameters.AddWithValue("$a", a);
        cmd.Parameters.AddWithValue("$b", b);
        var result = (long)cmd.ExecuteScalar()!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Schema_CreatesExpectedIndexes()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        var indexNames = new List<string>();
        while (reader.Read())
        {
            indexNames.Add(reader.GetString(0));
        }

        Assert.Contains("ix_torrents_path_key", indexNames);
        Assert.Contains("ix_torrents_category", indexNames);
        Assert.Contains("ix_torrents_state", indexNames);
        Assert.Contains("ix_torrent_files_hash", indexNames);
        Assert.Contains("ix_torrent_files_path_key", indexNames);
        Assert.Contains("ix_media_items_path_key", indexNames);
        Assert.Contains("ix_media_items_external_key", indexNames);
        Assert.Contains("ix_watch_history_path_key", indexNames);
        Assert.Contains("ix_watch_history_watched_at", indexNames);
    }

    [Fact]
    public void Schema_CreatesExpectedTablesAndView()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT name, type FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        var objects = new Dictionary<string, string>();
        while (reader.Read())
        {
            objects[reader.GetString(0)] = reader.GetString(1);
        }

        Assert.Equal("table", objects["torrents"]);
        Assert.Equal("table", objects["torrent_files"]);
        Assert.Equal("table", objects["media_items"]);
        Assert.Equal("table", objects["watch_history"]);
        Assert.Equal("view", objects["play_counts"]);
        Assert.Equal("table", objects["storage_paths"]);
    }
}
