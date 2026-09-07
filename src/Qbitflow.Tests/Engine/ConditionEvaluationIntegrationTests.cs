using System.Text.Json;
using Qbitflow.Core.Domain.Conditions;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Engine.Conditions;
using Qbitflow.Snapshot;
using Xunit;

namespace Qbitflow.Tests.Engine;

/// <summary>
/// Compiles condition trees and actually executes them against a real SnapshotDatabase
/// built from fixture data -- this is the "manual test against a live snapshot" the plan
/// calls for, done as a repeatable integration test instead of a UI walkthrough since the
/// Rules UI doesn't exist until Phase 7.
/// </summary>
public class ConditionEvaluationIntegrationTests : IDisposable
{
    private readonly SnapshotDatabase _db = new();
    private readonly ConditionSqlCompiler _compiler = new();

    public void Dispose() => _db.Dispose();

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static ComparisonNode Cmp(string field, ComparisonOperator op, JsonElement? value = null) =>
        new() { Field = field, Operator = op, Value = value };

    [Fact]
    public async Task CategoryFilter_MatchesOnlyTorrentsInThatCategory()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents =
            [
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "h-linux", Name = "Ubuntu", Category = "linux", SizeBytes = 1, Progress = 1 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "h-tv", Name = "Show S01E01", Category = "tv", SizeBytes = 1, Progress = 1 }
            ]
        });

        var query = _compiler.Compile(Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("linux")));
        var matches = await _compiler.ExecuteAsync(_db, query);

        var match = Assert.Single(matches);
        Assert.Equal("h-linux", match.TorrentHash);
    }

    [Fact]
    public async Task SizeGb_UdfComparison_MatchesLargeTorrentsOnly()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents =
            [
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "big", Name = "Big", SizeBytes = 10_000_000_000, Progress = 1 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "small", Name = "Small", SizeBytes = 100_000_000, Progress = 1 }
            ]
        });

        var query = _compiler.Compile(Cmp("qbittorrent.*.size_gb", ComparisonOperator.Gt, Json(5.0)));
        var matches = await _compiler.ExecuteAsync(_db, query);

        var match = Assert.Single(matches);
        Assert.Equal("big", match.TorrentHash);
    }

    [Fact]
    public async Task StorageCondition_AppliesUniformlyAcrossAllTorrents()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents =
            [
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "a", Name = "A", SizeBytes = 1, Progress = 1 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "b", Name = "B", SizeBytes = 1, Progress = 1 }
            ],
            StoragePaths =
            [
                new StorageUsageRecord { StoragePathId = 1, Name = "downloads", Path = "/downloads", Available = true, UsedPercent = 92.0, TotalBytes = 100, UsedBytes = 92, FreeBytes = 8 }
            ]
        });

        var query = _compiler.Compile(Cmp("storage.downloads.used_percent", ComparisonOperator.Gt, Json(85)));
        var matches = await _compiler.ExecuteAsync(_db, query);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public async Task NotExistsWatchHistory_MatchesTorrentsNeverWatchedOrWatchedLongAgo()
    {
        var rules = new List<Core.Domain.PathMappingRule>
        {
            new() { SourcePrefix = "/downloads", CanonicalPrefix = "/media" }
        };

        _db.Rebuild(new SnapshotInput
        {
            PathMappingRules = rules,
            Torrents =
            [
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "recently-watched", Name = "Recent", ContentPath = "/downloads/recent.mkv", SizeBytes = 1, Progress = 1 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "watched-long-ago", Name = "OldWatch", ContentPath = "/downloads/old.mkv", SizeBytes = 1, Progress = 1 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "never-watched", Name = "Never", ContentPath = "/downloads/never.mkv", SizeBytes = 1, Progress = 1 }
            ],
            WatchHistory =
            [
                new WatchHistoryRecord { InstanceId = 2, InstanceName = "tautulli", SourceType = SourceType.Tautulli, FilePath = "/media/recent.mkv", WatchedAt = DateTimeOffset.UtcNow.AddDays(-5) },
                new WatchHistoryRecord { InstanceId = 2, InstanceName = "tautulli", SourceType = SourceType.Tautulli, FilePath = "/media/old.mkv", WatchedAt = DateTimeOffset.UtcNow.AddDays(-200) }
            ]
        });

        // "no watch history in the last 90 days"
        var tree = new ExistsNode
        {
            Source = "tautulli.*",
            Negate = true,
            Condition = Cmp("tautulli.*.days_since_watched", ComparisonOperator.Lte, Json(90.0))
        };

        var query = _compiler.Compile(tree);
        var matches = await _compiler.ExecuteAsync(_db, query);

        var hashes = matches.Select(m => m.TorrentHash).ToHashSet();
        Assert.DoesNotContain("recently-watched", hashes);
        Assert.Contains("watched-long-ago", hashes);
        Assert.Contains("never-watched", hashes);
        Assert.Equal(2, hashes.Count);
    }

    [Fact]
    public async Task TargetInstanceIds_ScopesMatchesToThoseInstancesOnly()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents =
            [
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt-1", Hash = "same-hash", Name = "A", Category = "linux", SizeBytes = 1, Progress = 1 },
                new TorrentRecord { InstanceId = 2, InstanceName = "qbt-2", Hash = "same-hash", Name = "A", Category = "linux", SizeBytes = 1, Progress = 1 }
            ]
        });

        var query = _compiler.Compile(Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("linux")), targetInstanceIds: [1]);
        var matches = await _compiler.ExecuteAsync(_db, query);

        var match = Assert.Single(matches);
        Assert.Equal(1, match.InstanceId);
    }

    [Fact]
    public async Task AndOrNestedGroups_EvaluateWithCorrectPrecedence()
    {
        _db.Rebuild(new SnapshotInput
        {
            Torrents =
            [
                // matches: state=uploading AND (category=linux OR category=tv)
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "match-1", Name = "A", Category = "linux", State = "uploading", SizeBytes = 1, Progress = 1 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "match-2", Name = "B", Category = "tv", State = "uploading", SizeBytes = 1, Progress = 1 },
                // wrong state -- should not match despite category matching
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "no-match-state", Name = "C", Category = "linux", State = "downloading", SizeBytes = 1, Progress = 1 },
                // wrong category -- should not match despite state matching
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "no-match-category", Name = "D", Category = "movies", State = "uploading", SizeBytes = 1, Progress = 1 }
            ]
        });

        var tree = new GroupNode
        {
            Operator = LogicalOperator.And,
            Children =
            [
                Cmp("qbittorrent.*.state", ComparisonOperator.Eq, Json("uploading")),
                new GroupNode
                {
                    Operator = LogicalOperator.Or,
                    Children =
                    [
                        Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("linux")),
                        Cmp("qbittorrent.*.category", ComparisonOperator.Eq, Json("tv"))
                    ]
                }
            ]
        };

        var query = _compiler.Compile(tree);
        var matches = await _compiler.ExecuteAsync(_db, query);

        var hashes = matches.Select(m => m.TorrentHash).ToHashSet();
        Assert.Equal(new HashSet<string> { "match-1", "match-2" }, hashes);
    }

    [Fact]
    public async Task TopLevelRelatedSourceField_CorrelatesWithoutAnExplicitCheck()
    {
        SeedWatchHistoryFixture();

        // The same question as the NOT EXISTS above, written as a plain negated comparison --
        // this is the auto-correlation the compiler does for a related source's row field.
        var tree = new NotNode { Child = Cmp("tautulli.*.days_since_watched", ComparisonOperator.Lte, Json(90.0)) };

        var matches = await _compiler.ExecuteAsync(_db, _compiler.Compile(tree));

        var hashes = matches.Select(m => m.TorrentHash).ToHashSet();
        Assert.Equal(new HashSet<string> { "watched-long-ago", "never-watched" }, hashes);
    }

    [Fact]
    public async Task NamedInstance_MatchesOnlyThatInstancesRows()
    {
        SeedWatchHistoryFixture();

        var matches = await _compiler.ExecuteAsync(_db,
            _compiler.Compile(Cmp("tautulli.other.days_since_watched", ComparisonOperator.Lte, Json(90.0))));

        Assert.Empty(matches);
    }

    [Fact]
    public async Task RowsGoToTheTableOfTheSourceTypeThatProducedThem()
    {
        SeedWatchHistoryFixture();

        // The fixture's history came from Tautulli, so asking Jellystat finds nothing -- this is
        // the pooling the per-type layout removes.
        var matches = await _compiler.ExecuteAsync(_db,
            _compiler.Compile(Cmp("jellystat.*.days_since_watched", ComparisonOperator.Lte, Json(90.0))));

        Assert.Empty(matches);
    }

    [Fact]
    public async Task PlayCountAggregate_IsZeroForANeverWatchedTorrent()
    {
        SeedWatchHistoryFixture();

        var matches = await _compiler.ExecuteAsync(_db,
            _compiler.Compile(Cmp("tautulli.*.play_count", ComparisonOperator.Eq, Json(0))));

        var match = Assert.Single(matches);
        Assert.Equal("never-watched", match.TorrentHash);
    }

    [Fact]
    public async Task MediaAndHistoryRowsOfOneSource_DoNotLeakIntoEachOthersFields()
    {
        _db.Rebuild(new SnapshotInput
        {
            PathMappingRules = [new() { SourcePrefix = "/downloads", CanonicalPrefix = "/media" }],
            Torrents =
            [
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "in-library", Name = "A", ContentPath = "/downloads/a.mkv", SizeBytes = 1, Progress = 1 },
                new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "not-in-library", Name = "B", ContentPath = "/downloads/b.mkv", SizeBytes = 1, Progress = 1 }
            ],
            MediaItems =
            [
                new MediaItemRecord
                {
                    InstanceId = 5, InstanceName = "jf1", SourceType = SourceType.Jellyfin,
                    ExternalKey = "1", Title = "A", MediaType = "movie", FilePaths = ["/media/a.mkv"]
                }
            ]
        });

        var inLibrary = await _compiler.ExecuteAsync(_db,
            _compiler.Compile(Cmp("jellyfin.*.media_count", ComparisonOperator.Gt, Json(0))));
        Assert.Equal("in-library", Assert.Single(inLibrary).TorrentHash);

        // A media row has no watched_at, so a history field must not see it.
        var watched = await _compiler.ExecuteAsync(_db,
            _compiler.Compile(Cmp("jellyfin.*.play_count", ComparisonOperator.Gt, Json(0))));
        Assert.Empty(watched);
    }

    /// <summary>Three torrents -- watched recently, watched long ago, never watched -- with Tautulli history.</summary>
    private void SeedWatchHistoryFixture() => _db.Rebuild(new SnapshotInput
    {
        PathMappingRules = [new() { SourcePrefix = "/downloads", CanonicalPrefix = "/media" }],
        Torrents =
        [
            new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "recently-watched", Name = "Recent", ContentPath = "/downloads/recent.mkv", SizeBytes = 1, Progress = 1 },
            new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "watched-long-ago", Name = "OldWatch", ContentPath = "/downloads/old.mkv", SizeBytes = 1, Progress = 1 },
            new TorrentRecord { InstanceId = 1, InstanceName = "qbt", Hash = "never-watched", Name = "Never", ContentPath = "/downloads/never.mkv", SizeBytes = 1, Progress = 1 }
        ],
        WatchHistory =
        [
            new WatchHistoryRecord { InstanceId = 2, InstanceName = "tautulli", SourceType = SourceType.Tautulli, FilePath = "/media/recent.mkv", WatchedAt = DateTimeOffset.UtcNow.AddDays(-5) },
            new WatchHistoryRecord { InstanceId = 2, InstanceName = "tautulli", SourceType = SourceType.Tautulli, FilePath = "/media/old.mkv", WatchedAt = DateTimeOffset.UtcNow.AddDays(-200) }
        ]
    });
}
