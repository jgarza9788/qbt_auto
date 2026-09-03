using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;
using QbitFlow.Core.Domain;
using QbitFlow.Engine;
using QbitFlow.Engine.Pipelines;
using QbitFlow.Infrastructure;
using QbitFlow.Infrastructure.Data;
using QbitFlow.Tests.Actions;

namespace QbitFlow.Tests.Engine;

public class PipelineRunnerTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"qbitflow-runner-{Guid.NewGuid():N}.db");
    private ServiceProvider _sp = null!;
    private readonly FakeGatewayFactory _gateways = new();

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQbitFlowInfrastructure($"Data Source={_dbPath}");
        services.AddQbitFlowEngine();
        services.RemoveAll<IQbtGatewayFactory>();
        services.AddSingleton<IQbtGatewayFactory>(_gateways);
        _sp = services.BuildServiceProvider();

        await using var scope = _sp.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private async Task<Guid> SeedPipelineAsync(bool dryRun, string criteria, string actionType, string paramsJson)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var qbt = new SourceConnection { Name = "qbt", Kind = SourceKind.Qbt, BaseUrl = "http://x" };
        db.SourceConnections.Add(qbt);

        var pipeline = new Pipeline { Name = "p", Enabled = true, DryRun = dryRun, IntervalSeconds = 900 };
        pipeline.Sources.Add(new PipelineSource { SourceConnectionId = qbt.Id, Roles = PipelineSourceRoles.Data | PipelineSourceRoles.ActionTarget });
        pipeline.Rules.Add(new Rule
        {
            Name = "r", Order = 0, Enabled = true,
            ConditionMode = RuleConditionMode.Raw, RawExpression = criteria, CompiledExpression = criteria,
            Action = new RuleAction { Type = actionType, ParamsJson = paramsJson },
        });
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync();

        _gateways.Target = new FakeQbtActionTarget();
        _gateways.Torrents =
        [
            new TorrentView { Hash = "h1", Name = "Small.File", Category = "x", Size = 500, Tags = [] },
            new TorrentView { Hash = "h2", Name = "Big.File", Category = "x", Size = 5_000_000_000, Tags = [] },
        ];
        return pipeline.Id;
    }

    [Fact]
    public async Task Dry_run_evaluates_but_applies_nothing()
    {
        var id = await SeedPipelineAsync(dryRun: true, "(<Size> < 1073741824)", "tag.sync", "{\"tag\":\"small\"}");

        await using var scope = _sp.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<IPipelineRunner>();
        var runId = await runner.RunAsync(id, RunTrigger.Manual, dryRunOverride: null, CancellationToken.None);

        _gateways.Target!.Calls.Should().BeEmpty();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.RunHistory.Include(r => r.RuleResults).FirstAsync(r => r.Id == runId);
        run.Status.Should().Be(RunStatus.Succeeded);
        run.TorrentsEvaluated.Should().Be(2);
        run.ActionsWouldApply.Should().Be(1);   // only the small file
        run.ActionsApplied.Should().Be(0);
        run.RuleResults.Single().SuccessCount.Should().Be(1);
        run.RuleResults.Single().FailureCount.Should().Be(1);
    }

    [Fact]
    public async Task Live_run_applies_the_matching_action_only()
    {
        var id = await SeedPipelineAsync(dryRun: false, "(<Size> < 1073741824)", "tag.sync", "{\"tag\":\"small\"}");

        await using var scope = _sp.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<IPipelineRunner>();
        var runId = await runner.RunAsync(id, RunTrigger.Manual, dryRunOverride: null, CancellationToken.None);

        _gateways.Target!.Calls.Should().ContainSingle().Which.Should().Be("addTag:h1:small");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.RunHistory.FirstAsync(r => r.Id == runId);
        run.ActionsApplied.Should().Be(1);

        var pipeline = await db.Pipelines.FirstAsync(p => p.Id == id);
        pipeline.NextRunUtc.Should().NotBeNull();
        pipeline.IsRunning.Should().BeFalse();
    }

    private sealed class FakeGatewayFactory : IQbtGatewayFactory, IQbtAdapter
    {
        public FakeQbtActionTarget? Target;
        public IReadOnlyList<TorrentView> Torrents = [];

        public Guid SourceId => Guid.Empty;
        public IQbtAdapter GetAdapter(Guid id) => this;
        public IQbtActionTarget GetActionTarget(Guid id) => Target!;
        public Task<HealthResult> TestAsync(CancellationToken ct) => Task.FromResult(HealthResult.Healthy(1));
        public Task<IReadOnlyList<TorrentView>> FetchTorrentsAsync(CancellationToken ct) => Task.FromResult(Torrents);
    }
}
