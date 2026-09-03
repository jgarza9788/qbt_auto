using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Actions;

namespace QbitFlow.Tests.Actions;

public class ActionHandlerTests
{
    private static TorrentView Torrent(string category = "Movies", string[]? tags = null) => new()
    {
        Hash = "abc",
        Name = "Some.Movie.2024.1080p",
        Category = category,
        Tags = tags ?? [],
    };

    private static ActionContext Ctx(FakeQbtActionTarget qbt, bool? match, TorrentView torrent, bool dryRun, params (string, string)[] p) => new()
    {
        RuleId = Guid.NewGuid(),
        RuleName = "test",
        Match = match,
        Torrent = torrent,
        Fields = new Dictionary<string, object?>(),
        Params = p.ToDictionary(x => x.Item1, x => x.Item2),
        Qbt = qbt,
        DryRun = dryRun,
        Log = NullLogger.Instance,
        CancellationToken = CancellationToken.None,
    };

    [Fact]
    public async Task TagSync_adds_on_true_and_removes_on_false()
    {
        var qbt = new FakeQbtActionTarget();
        var h = new TagSyncActionHandler();

        (await h.ApplyAsync(Ctx(qbt, true, Torrent(tags: []), dryRun: false, ("tag", "cold"))))
            .Should().Be(ActionOutcome.Applied);
        qbt.Calls.Should().ContainSingle().Which.Should().Be("addTag:abc:cold");

        qbt.Calls.Clear();
        (await h.ApplyAsync(Ctx(qbt, false, Torrent(tags: ["cold"]), dryRun: false, ("tag", "cold"))))
            .Should().Be(ActionOutcome.Applied);
        qbt.Calls.Should().ContainSingle().Which.Should().Be("removeTag:abc:cold");
    }

    [Fact]
    public async Task TagSync_is_noop_when_already_in_desired_state()
    {
        var qbt = new FakeQbtActionTarget();
        var h = new TagSyncActionHandler();

        (await h.ApplyAsync(Ctx(qbt, true, Torrent(tags: ["cold"]), dryRun: false, ("tag", "cold"))))
            .Should().Be(ActionOutcome.Skipped);
        qbt.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DryRun_makes_no_calls_and_reports_WouldApply()
    {
        var qbt = new FakeQbtActionTarget();
        var h = new CategorySetActionHandler();

        (await h.ApplyAsync(Ctx(qbt, true, Torrent(category: "unsorted"), dryRun: true, ("category", "Movies"))))
            .Should().Be(ActionOutcome.WouldApply);
        qbt.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Null_match_is_NotApplicable()
    {
        var qbt = new FakeQbtActionTarget();
        var h = new TagAddActionHandler();

        (await h.ApplyAsync(Ctx(qbt, null, Torrent(), dryRun: false, ("tag", "x"))))
            .Should().Be(ActionOutcome.NotApplicable);
        qbt.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SpeedLimit_pauses_when_both_zero()
    {
        var qbt = new FakeQbtActionTarget();
        var h = new SpeedLimitActionHandler();

        (await h.ApplyAsync(Ctx(qbt, true, Torrent(), dryRun: false, ("uploadKb", "0"), ("downloadKb", "0"))))
            .Should().Be(ActionOutcome.Applied);
        qbt.Calls.Should().ContainSingle().Which.Should().Be("pause:abc");
    }

    [Fact]
    public void ActionRegistry_rejects_duplicate_types()
    {
        var act = () => new ActionRegistry([new TagAddActionHandler(), new TagAddActionHandler()]);
        act.Should().Throw<InvalidOperationException>();
    }
}
