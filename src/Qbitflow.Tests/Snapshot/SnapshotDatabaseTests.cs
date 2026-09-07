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
        cmd.CommandText = "SELECT hash, name, category, tags, path_key, size_bytes FROM qbittorrent";
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
    public void Rebuild_InsertsExtendedTorrentFields_AndDerivedColumnsCompute()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents =
            [
                new TorrentRecord
                {
                    InstanceId = 1, InstanceName = "Main", Hash = "h1", Name = "T", SizeBytes = 1, Progress = 1,
                    Tracker = "udp://tracker.example.org:451/announce",
                    TotalSizeBytes = 8_000_000_000, SeedingTimeSeconds = 172_800, ActiveTimeSeconds = 259_200,
                    TotalSeeds = 0, ConnectedSeeds = 0, AutoTmmEnabled = true, RatioLimit = -2,
                    LastActivityOn = DateTimeOffset.UtcNow.AddDays(-5)
                }
            ]
        });

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT tracker, total_size_bytes, auto_tmm FROM qbittorrent WHERE hash = 'h1'";
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal("udp://tracker.example.org:451/announce", reader.GetString(0));
            Assert.Equal(8_000_000_000, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
        }

        using var derived = _db.Connection.CreateCommand();
        derived.CommandText = "SELECT seeding_time_seconds / 86400.0, days_since(last_activity) FROM qbittorrent WHERE hash = 'h1'";
        using var dr = derived.ExecuteReader();
        Assert.True(dr.Read());
        Assert.Equal(2.0, dr.GetDouble(0), precision: 6);
        Assert.InRange(dr.GetDouble(1), 4.9, 5.1);
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
            FROM qbittorrent t
            JOIN plex m ON t.path_key = m.path_key AND m.kind = 'media'
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
        cmd.CommandText = "SELECT hash FROM qbittorrent";
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
        cmd.CommandText = "SELECT available, error FROM storage WHERE storage_path_id = 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal("Path does not exist or is not mounted.", reader.GetString(1));
    }

    [Fact]
    public void WatchEvents_AggregateByPathKey_WithinTheirOwnSourceTypeTable()
    {
        _db.Rebuild(new SnapshotInput
        {
            WatchHistory =
            [
                new WatchHistoryRecord { InstanceId = 1, InstanceName = "t", SourceType = SourceType.Tautulli, FilePath = "/media/foo.mkv", UserName = "alice", WatchedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), PercentComplete = 100 },
                new WatchHistoryRecord { InstanceId = 1, InstanceName = "t", SourceType = SourceType.Tautulli, FilePath = "/media/foo.mkv", UserName = "bob", WatchedAt = DateTimeOffset.Parse("2026-01-05T00:00:00Z"), PercentComplete = 95 },
                new WatchHistoryRecord { InstanceId = 1, InstanceName = "t", SourceType = SourceType.Tautulli, FilePath = "/media/bar.mkv", UserName = "alice", WatchedAt = DateTimeOffset.Parse("2026-01-02T00:00:00Z"), PercentComplete = 100 }
            ]
        });

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), COUNT(DISTINCT user_name)
            FROM tautulli
            WHERE kind = 'history' AND path_key = '/media/foo.mkv'
            """;
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

        Assert.Contains("ix_qbittorrent_path_key", indexNames);
        Assert.Contains("ix_qbittorrent_instance", indexNames);
        Assert.Contains("ix_qbittorrent_category", indexNames);
        Assert.Contains("ix_qbittorrent_state", indexNames);
        Assert.Contains("ix_qbittorrent_files_hash", indexNames);
        Assert.Contains("ix_qbittorrent_files_path_key", indexNames);
        Assert.Contains("ix_storage_instance", indexNames);

        // Every media/history type gets the same pair, generated from the enum.
        foreach (var type in SourceNaming.MediaHistoryTypes)
        {
            var table = SourceNaming.TypeKey(type);
            Assert.Contains($"ix_{table}_path_key", indexNames);
            Assert.Contains($"ix_{table}_instance", indexNames);
        }
    }

    [Fact]
    public void Schema_CreatesOneTablePerSourceType()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT name, type FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        var objects = new Dictionary<string, string>();
        while (reader.Read())
        {
            objects[reader.GetString(0)] = reader.GetString(1);
        }

        Assert.Equal("table", objects["qbittorrent"]);
        Assert.Equal("table", objects["qbittorrent_files"]);
        Assert.Equal("table", objects["storage"]);

        // One table per source type is the whole point: a field key's <type> segment IS a
        // table name, so every enum value must have one.
        foreach (var type in SourceNaming.MediaHistoryTypes)
        {
            Assert.Equal("table", objects[SourceNaming.TypeKey(type)]);
        }

        // The shape-pooled tables are gone, not renamed.
        Assert.DoesNotContain("media_items", objects.Keys);
        Assert.DoesNotContain("watch_history", objects.Keys);
        Assert.DoesNotContain("play_counts", objects.Keys);
    }

    [Fact]
    public void Rebuild_RoutesEachRecordToItsOwnSourceTypesTable()
    {
        _db.Rebuild(new SnapshotInput
        {
            MediaItems =
            [
                new MediaItemRecord
                {
                    InstanceId = 1, InstanceName = "jf1", SourceType = SourceType.Jellyfin,
                    ExternalKey = "1", Title = "Library item", FilePaths = ["/media/a.mkv"]
                }
            ],
            WatchHistory =
            [
                new WatchHistoryRecord
                {
                    InstanceId = 2, InstanceName = "js1", SourceType = SourceType.Jellystat,
                    MediaTitle = "Watched item", FilePath = "/media/a.mkv",
                    UserName = "alice", WatchedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                }
            ]
        });

        Assert.Equal([("media", "Library item", "jf1")], Rows("jellyfin"));
        Assert.Equal([("history", "Watched item", "js1")], Rows("jellystat"));

        // A type with no rows still has an empty table rather than not existing.
        Assert.Empty(Rows("tautulli"));
        Assert.Empty(Rows("streamystats"));
    }

    private List<(string Kind, string Title, string Instance)> Rows(string table)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT kind, title, instance FROM {table}";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return rows;
    }
}
