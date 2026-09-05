using Microsoft.Extensions.Logging.Abstractions;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.Actions;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Engine.Actions;
using Qbitflow.Engine.Conditions;
using Qbitflow.Tests.TestHelpers;
using Xunit;

namespace Qbitflow.Tests.Engine;

public class ActionExecutorTests
{
    private static SourceConnectionInfo Connection(int instanceId = 1) => new()
    {
        InstanceId = instanceId,
        InstanceName = "qbt",
        SourceType = SourceType.Qbittorrent,
        BaseUrl = "http://qbt.local"
    };

    [Fact]
    public async Task AddTags_SkipsTorrentsAlreadyTagged_AppliesOnlyToOthers()
    {
        var client = new FakeQbtActionClient();
        client.State["already-tagged"] = new QbtTorrentState { Hash = "already-tagged", Tags = ["done"] };
        client.State["needs-tag"] = new QbtTorrentState { Hash = "needs-tag" };

        var executor = new ActionExecutor(client, NullLogger<ActionExecutor>.Instance);
        var matches = new List<MatchedTorrent> { new(1, "already-tagged"), new(1, "needs-tag") };
        var actions = new List<ActionDefinition> { new AddTagsAction { Tags = ["done"] } };

        var summary = await executor.ExecuteAsync(actions, new Dictionary<int, SourceConnectionInfo> { [1] = Connection() }, matches, dryRun: false);

        var skipped = Assert.Single(summary.Results, r => r.Outcome == ActionOutcome.SkippedAlreadyMatching);
        Assert.Equal("already-tagged", skipped.TorrentHash);
        var applied = Assert.Single(summary.Results, r => r.Outcome == ActionOutcome.Applied);
        Assert.Equal("needs-tag", applied.TorrentHash);

        var call = Assert.Single(client.Calls);
        Assert.Equal("AddTags:needs-tag:done", call);
    }

    [Fact]
    public async Task RunningSameActionTwice_SecondRunIsANoOp()
    {
        var client = new FakeQbtActionClient();
        client.State["h1"] = new QbtTorrentState { Hash = "h1" };

        var executor = new ActionExecutor(client, NullLogger<ActionExecutor>.Instance);
        var matches = new List<MatchedTorrent> { new(1, "h1") };
        var actions = new List<ActionDefinition> { new SetCategoryAction { Category = "archived" } };
        var instances = new Dictionary<int, SourceConnectionInfo> { [1] = Connection() };

        var first = await executor.ExecuteAsync(actions, instances, matches, dryRun: false);
        Assert.Equal(1, first.AppliedCount);
        Assert.Single(client.Calls);

        var second = await executor.ExecuteAsync(actions, instances, matches, dryRun: false);
        Assert.Equal(0, second.AppliedCount);
        Assert.Equal(1, second.SkippedCount);
        Assert.Single(client.Calls); // still just the one call from the first run
    }

    [Fact]
    public async Task RemoveTags_SkipsTorrentsWithoutTheTag()
    {
        var client = new FakeQbtActionClient();
        client.State["h1"] = new QbtTorrentState { Hash = "h1" };

        var executor = new ActionExecutor(client, NullLogger<ActionExecutor>.Instance);
        var matches = new List<MatchedTorrent> { new(1, "h1") };
        var actions = new List<ActionDefinition> { new RemoveTagsAction { Tags = ["stale"] } };

        var summary = await executor.ExecuteAsync(actions, new Dictionary<int, SourceConnectionInfo> { [1] = Connection() }, matches, dryRun: false);

        Assert.Equal(1, summary.SkippedCount);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task SpeedLimits_AreIdempotent()
    {
        var client = new FakeQbtActionClient();
        client.State["h1"] = new QbtTorrentState { Hash = "h1", UploadLimitBytesPerSec = 1000 };

        var executor = new ActionExecutor(client, NullLogger<ActionExecutor>.Instance);
        var matches = new List<MatchedTorrent> { new(1, "h1") };
        var instances = new Dictionary<int, SourceConnectionInfo> { [1] = Connection() };

        var alreadySet = await executor.ExecuteAsync([new SetUploadLimitAction { LimitBytesPerSec = 1000 }], instances, matches, dryRun: false);
        Assert.Equal(1, alreadySet.SkippedCount);
        Assert.Empty(client.Calls);

        var changed = await executor.ExecuteAsync([new SetUploadLimitAction { LimitBytesPerSec = 2000 }], instances, matches, dryRun: false);
        Assert.Equal(1, changed.AppliedCount);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task DryRun_NeverCallsTheClient_ButReportsWhatWouldHappen()
    {
        var client = new FakeQbtActionClient();
        client.State["h1"] = new QbtTorrentState { Hash = "h1" };

        var executor = new ActionExecutor(client, NullLogger<ActionExecutor>.Instance);
        var matches = new List<MatchedTorrent> { new(1, "h1") };
        var actions = new List<ActionDefinition> { new SetCategoryAction { Category = "archived" } };

        var summary = await executor.ExecuteAsync(actions, new Dictionary<int, SourceConnectionInfo> { [1] = Connection() }, matches, dryRun: true);

        Assert.Equal(1, summary.DryRunCount);
        Assert.Empty(client.Calls);
        Assert.Null(client.State["h1"].Category); // unchanged
    }

    [Fact]
    public async Task UnknownInstance_ReportsFailureWithoutThrowing()
    {
        var client = new FakeQbtActionClient();
        var executor = new ActionExecutor(client, NullLogger<ActionExecutor>.Instance);
        var matches = new List<MatchedTorrent> { new(99, "h1") };
        var actions = new List<ActionDefinition> { new SetCategoryAction { Category = "archived" } };

        var summary = await executor.ExecuteAsync(actions, new Dictionary<int, SourceConnectionInfo>(), matches, dryRun: false);

        var result = Assert.Single(summary.Results);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task Move_WaitsForCompletion_BeforeReportingApplied()
    {
        var client = new FakeQbtActionClient { ConvergeAfterPolls = 0 };
        client.State["h1"] = new QbtTorrentState { Hash = "h1", SavePath = "/old" };

        var executor = new ActionExecutor(client, NullLogger<ActionExecutor>.Instance, movePollInterval: TimeSpan.FromMilliseconds(5), moveMaxAttempts: 5);
        var matches = new List<MatchedTorrent> { new(1, "h1") };
        var actions = new List<ActionDefinition> { new MoveAction { DestinationPath = "/new", WaitForCompletion = true } };

        var summary = await executor.ExecuteAsync(actions, new Dictionary<int, SourceConnectionInfo> { [1] = Connection() }, matches, dryRun: false);

        var result = Assert.Single(summary.Results);
        Assert.Equal(ActionOutcome.Applied, result.Outcome);
        Assert.Equal("/new", client.State["h1"].SavePath);
    }

    [Fact]
    public async Task Move_IsIdempotent_OnceAlreadyAtDestination()
    {
        var client = new FakeQbtActionClient();
        client.State["h1"] = new QbtTorrentState { Hash = "h1", SavePath = "/media/done" };

        var executor = new ActionExecutor(client, NullLogger<ActionExecutor>.Instance);
        var matches = new List<MatchedTorrent> { new(1, "h1") };
        var actions = new List<ActionDefinition> { new MoveAction { DestinationPath = "/media/done" } };

        var summary = await executor.ExecuteAsync(actions, new Dictionary<int, SourceConnectionInfo> { [1] = Connection() }, matches, dryRun: false);

        Assert.Equal(1, summary.SkippedCount);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Move_FailsWithTimeout_WhenItNeverConverges()
    {
        var client = new FakeQbtActionClient { ConvergeAfterPolls = 1000 };
        client.State["h1"] = new QbtTorrentState { Hash = "h1", SavePath = "/old" };

        var executor = new ActionExecutor(client, NullLogger<ActionExecutor>.Instance, movePollInterval: TimeSpan.FromMilliseconds(2), moveMaxAttempts: 2);
        var matches = new List<MatchedTorrent> { new(1, "h1") };
        var actions = new List<ActionDefinition> { new MoveAction { DestinationPath = "/new" } };

        var summary = await executor.ExecuteAsync(actions, new Dictionary<int, SourceConnectionInfo> { [1] = Connection() }, matches, dryRun: false);

        var result = Assert.Single(summary.Results);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Contains("did not complete", result.Error);
    }

    [Fact]
    public async Task ActionsAreBatched_OneCallCoversAllHashesForAnInstance()
    {
        var client = new FakeQbtActionClient();
        client.State["h1"] = new QbtTorrentState { Hash = "h1" };
        client.State["h2"] = new QbtTorrentState { Hash = "h2" };
        client.State["h3"] = new QbtTorrentState { Hash = "h3" };

        var executor = new ActionExecutor(client, NullLogger<ActionExecutor>.Instance);
        var matches = new List<MatchedTorrent> { new(1, "h1"), new(1, "h2"), new(1, "h3") };
        var actions = new List<ActionDefinition> { new SetCategoryAction { Category = "batch" } };

        var summary = await executor.ExecuteAsync(actions, new Dictionary<int, SourceConnectionInfo> { [1] = Connection() }, matches, dryRun: false);

        Assert.Equal(3, summary.AppliedCount);
        var call = Assert.Single(client.Calls);
        Assert.Equal("SetCategory:h1,h2,h3:batch", call);
    }
}
