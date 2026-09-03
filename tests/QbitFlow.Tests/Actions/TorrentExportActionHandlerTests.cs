using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Actions;

namespace QbitFlow.Tests.Actions;

public class TorrentExportActionHandlerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qf-export-{Guid.NewGuid():N}");
    private readonly FakeQbtActionTarget _qbt = new();

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private ActionContext Ctx(bool? match, bool dryRun) => new()
    {
        RuleId = Guid.NewGuid(),
        RuleName = "export",
        Match = match,
        Torrent = new TorrentView { Hash = "abc123", Name = "Some Movie 2024" },
        Fields = new Dictionary<string, object?>(),
        Params = new Dictionary<string, string> { ["folder"] = _dir },
        Qbt = _qbt,
        DryRun = dryRun,
        Log = NullLogger.Instance,
        CancellationToken = CancellationToken.None,
    };

    [Fact]
    public async Task Writes_the_torrent_file_on_match()
    {
        var outcome = await new TorrentExportActionHandler().ApplyAsync(Ctx(match: true, dryRun: false));

        outcome.Should().Be(ActionOutcome.Applied);
        _qbt.Calls.Should().Contain("export:abc123");
        var file = Path.Combine(_dir, "Some Movie 2024.torrent");
        File.Exists(file).Should().BeTrue();
        (await File.ReadAllBytesAsync(file)).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Skips_if_the_file_already_exists()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "Some Movie 2024.torrent"), "x");

        var outcome = await new TorrentExportActionHandler().ApplyAsync(Ctx(match: true, dryRun: false));

        outcome.Should().Be(ActionOutcome.Skipped);
        _qbt.Calls.Should().NotContain("export:abc123");
    }

    [Fact]
    public async Task DryRun_writes_nothing()
    {
        var outcome = await new TorrentExportActionHandler().ApplyAsync(Ctx(match: true, dryRun: true));

        outcome.Should().Be(ActionOutcome.WouldApply);
        _qbt.Calls.Should().BeEmpty();
        (Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []).Should().BeEmpty();
    }

    [Fact]
    public async Task Not_applicable_when_criteria_did_not_match()
    {
        (await new TorrentExportActionHandler().ApplyAsync(Ctx(match: false, dryRun: false)))
            .Should().Be(ActionOutcome.NotApplicable);
    }
}
