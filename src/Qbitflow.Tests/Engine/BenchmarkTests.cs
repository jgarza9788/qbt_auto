using System.Diagnostics;
using System.Text.Json;
using Qbitflow.Core.Domain.Conditions;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Engine.Conditions;
using Qbitflow.Snapshot;
using Xunit;

namespace Qbitflow.Tests.Engine;

/// <summary>
/// Proves the snapshot + condition compiler stay well under a second even at the scale
/// the spec calls out: ~10,000 torrents and ~50 rules. This is what "the matched set
/// comes back from one query, not a loop over every torrent" is actually for.
/// </summary>
public class BenchmarkTests
{
    private static readonly string[] Categories = ["linux", "tv", "movies", "music", "archived", "priority", "downloads"];
    private static readonly string[] States = ["downloading", "uploading", "pausedUP", "stalledUP", "error"];

    [Fact]
    public async Task TenThousandTorrents_FiftyRules_EvaluateWellUnderOneSecond()
    {
        using var db = new SnapshotDatabase();

        var torrents = BuildTorrents(10_000);
        var watchHistory = BuildWatchHistory(torrents, 2_000);
        db.Rebuild(new SnapshotInput { Torrents = torrents, WatchHistory = watchHistory });

        var compiler = new ConditionSqlCompiler();
        var rules = BuildRules(50);

        var sw = Stopwatch.StartNew();
        var totalMatches = 0;
        foreach (var tree in rules)
        {
            var compiled = compiler.Compile(tree);
            var matches = await compiler.ExecuteAsync(db, compiled);
            totalMatches += matches.Count;
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"50 rule evaluations over 10,000 torrents took {sw.ElapsedMilliseconds}ms, expected well under 1000ms.");

        // Sanity: rules should actually be matching a plausible slice of the fixture, not
        // all silently returning zero rows (which would make the timing meaningless).
        Assert.True(totalMatches > 0);
    }

    private static List<TorrentRecord> BuildTorrents(int count)
    {
        var list = new List<TorrentRecord>(count);
        var baseTime = DateTimeOffset.UtcNow;

        for (var i = 0; i < count; i++)
        {
            list.Add(new TorrentRecord
            {
                InstanceId = 1,
                InstanceName = "bench",
                Hash = $"hash-{i:D6}",
                Name = $"Torrent {i}",
                Category = Categories[i % Categories.Length],
                Tags = i % 5 == 0 ? ["verified"] : [],
                ContentPath = $"/downloads/torrent-{i}/file.mkv",
                SavePath = "/downloads",
                SizeBytes = (i % 50 + 1) * 1_000_000_000L,
                Progress = i % 10 == 0 ? 0.5 : 1.0,
                State = States[i % States.Length],
                DownloadedBytes = (i % 50 + 1) * 1_000_000_000L,
                UploadedBytes = i * 1_000_000L,
                Ratio = (i % 10) / 2.0,
                AddedOn = baseTime.AddDays(-(i % 400)),
                CompletionOn = baseTime.AddDays(-(i % 300)),
                UploadLimitBytesPerSec = 0,
                DownloadLimitBytesPerSec = 0
            });
        }

        return list;
    }

    private static List<WatchHistoryRecord> BuildWatchHistory(List<TorrentRecord> torrents, int count)
    {
        var list = new List<WatchHistoryRecord>(count);
        var baseTime = DateTimeOffset.UtcNow;

        for (var i = 0; i < count; i++)
        {
            var torrent = torrents[i * (torrents.Count / count)];
            list.Add(new WatchHistoryRecord
            {
                InstanceId = 2,
                InstanceName = "bench-tautulli",
                MediaTitle = torrent.Name,
                FilePath = torrent.ContentPath,
                UserName = i % 2 == 0 ? "alice" : "bob",
                WatchedAt = baseTime.AddDays(-(i % 200)),
                PercentComplete = 100
            });
        }

        return list;
    }

    private static List<ConditionNode> BuildRules(int count)
    {
        var rules = new List<ConditionNode>(count);

        for (var i = 0; i < count; i++)
        {
            ConditionNode tree = (i % 5) switch
            {
                0 => Cmp("category", ComparisonOperator.Eq, Categories[i % Categories.Length]),
                1 => new GroupNode
                {
                    Operator = LogicalOperator.And,
                    Children =
                    [
                        Cmp("state", ComparisonOperator.Eq, States[i % States.Length]),
                        Cmp("size_gb", ComparisonOperator.Gt, 5.0)
                    ]
                },
                2 => new GroupNode
                {
                    Operator = LogicalOperator.Or,
                    Children =
                    [
                        Cmp("category", ComparisonOperator.Eq, "linux"),
                        Cmp("category", ComparisonOperator.Eq, "tv")
                    ]
                },
                3 => new ExistsNode
                {
                    Relation = "watch_history",
                    Negate = true,
                    Condition = Cmp("days_since_watched", ComparisonOperator.Lte, 90.0)
                },
                _ => Cmp("days_since_added", ComparisonOperator.Gt, 30.0)
            };

            rules.Add(tree);
        }

        return rules;
    }

    private static ComparisonNode Cmp(string field, ComparisonOperator op, object value) =>
        new() { Field = field, Operator = op, Value = JsonSerializer.SerializeToElement(value) };
}
