using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;
using QbitFlow.Core.Domain;
using QbitFlow.Engine;
using QbitFlow.Engine.RuleEngine;
using QbitFlow.Infrastructure;
using QbitFlow.Infrastructure.Data;
using QbitFlow.Tests.Actions;

namespace QbitFlow.Tests.Engine;

public class RuleEngineRunnerTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"qbitflow-engine-{Guid.NewGuid():N}.db");
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

    private async Task<Guid> SeedAsync(string criteria, string actionType, string paramsJson,
        bool enabled = true, int order = 0, bool? stopOnMatch = null, int? cooldown = null)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var qbt = await db.SourceConnections.FirstOrDefaultAsync(s => s.Kind == SourceKind.Qbt);
        if (qbt is null)
        {
            qbt = new SourceConnection { Name = "qbt", Kind = SourceKind.Qbt, BaseUrl = "http://x", Enabled = true };
            db.SourceConnections.Add(qbt);
        }

        var rule = new Rule
        {
            Name = "r" + order, Order = order, Enabled = enabled, StopOnMatch = stopOnMatch, CooldownSeconds = cooldown,
            ConditionMode = RuleConditionMode.Raw, RawExpression = criteria, CompiledExpression = criteria,
            Action = new RuleAction { Type = actionType, ParamsJson = paramsJson },
        };
        db.Rules.Add(rule);
        await db.SaveChangesAsync();

        _gateways.Target = new FakeQbtActionTarget();
        _gateways.Torrents =
        [
            new TorrentView { Hash = "h1", Name = "Small.File", Category = "x", Size = 500, Tags = [] },
            new TorrentView { Hash = "h2", Name = "Big.File", Category = "x", Size = 5_000_000_000, Tags = [] },
        ];
        return rule.Id;
    }

    private IRuleEngineRunner Runner(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IRuleEngineRunner>();

    [Fact]
    public async Task No_enabled_rules_produces_no_run()
    {
        await SeedAsync("(<Size> < 1073741824)", "tag.sync", "{\"tag\":\"small\"}", enabled: false);

        await using var scope = _sp.CreateAsyncScope();
        var runId = await Runner(scope).RunAsync(RunTrigger.Manual, dryRunOverride: null, CancellationToken.None);

        runId.Should().BeNull();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().RunHistory.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Dry_run_evaluates_but_applies_nothing()
    {
        await SeedAsync("(<Size> < 1073741824)", "tag.sync", "{\"tag\":\"small\"}");

        await using var scope = _sp.CreateAsyncScope();
        var runId = await Runner(scope).RunAsync(RunTrigger.Manual, dryRunOverride: true, CancellationToken.None);

        _gateways.Target!.Calls.Should().BeEmpty();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.RunHistory.Include(r => r.RuleResults).FirstAsync(r => r.Id == runId);
        run.Status.Should().Be(RunStatus.Succeeded);
        run.TorrentsEvaluated.Should().Be(2);
        run.ActionsWouldApply.Should().Be(1);
        run.ActionsApplied.Should().Be(0);
        run.RuleResults.Single().SuccessCount.Should().Be(1);
        run.RuleResults.Single().FailureCount.Should().Be(1);
    }

    [Fact]
    public async Task Live_run_applies_the_matching_action_only()
    {
        await SeedAsync("(<Size> < 1073741824)", "tag.sync", "{\"tag\":\"small\"}");

        await using var scope = _sp.CreateAsyncScope();
        var runId = await Runner(scope).RunAsync(RunTrigger.Manual, dryRunOverride: false, CancellationToken.None);

        _gateways.Target!.Calls.Should().ContainSingle().Which.Should().Be("addTag:h1:small");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.RunHistory.FirstAsync(r => r.Id == runId)).ActionsApplied.Should().Be(1);
    }

    [Fact]
    public async Task Cooldown_suppresses_a_second_live_fire()
    {
        await SeedAsync("(<Size> < 1073741824)", "tag.sync", "{\"tag\":\"small\"}", cooldown: 3600);

        await using var scope = _sp.CreateAsyncScope();
        var runner = Runner(scope);
        await runner.RunAsync(RunTrigger.Manual, dryRunOverride: false, CancellationToken.None);
        _gateways.Target!.Calls.Clear();

        var runId = await runner.RunAsync(RunTrigger.Manual, dryRunOverride: false, CancellationToken.None);

        _gateways.Target!.Calls.Should().BeEmpty();   // still in cooldown
        var run = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .RunHistory.FirstAsync(r => r.Id == runId);
        run.ActionsApplied.Should().Be(0);
        run.ActionsSkipped.Should().Be(1);
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
