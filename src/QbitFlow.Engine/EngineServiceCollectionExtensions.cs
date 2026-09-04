using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Expressions;
using QbitFlow.Engine.Actions;
using QbitFlow.Engine.Analytics;
using QbitFlow.Engine.Derived;
using QbitFlow.Engine.Evaluation;
using QbitFlow.Engine.Health;
using QbitFlow.Engine.Matching;
using QbitFlow.Engine.RuleEngine;
using QbitFlow.Engine.Sources;

namespace QbitFlow.Engine;

public static class EngineServiceCollectionExtensions
{
    public static IServiceCollection AddQbitFlowEngine(this IServiceCollection services)
    {
        services.AddSingleton<CriteriaEvaluator>();
        services.AddSingleton<ConditionCompiler>();
        services.AddSingleton<DriveDataProvider>();

        // Media matching + watch-popularity analytics.
        services.AddSingleton<IMediaMatcher, FilenameMediaMatcher>();
        services.AddSingleton<IAnalyticsService, AnalyticsService>();
        services.AddSingleton<AnalyticsRefreshService>();
        services.AddSingleton<IMediaEnricher, CachedMediaEnricher>();

        services.AddScoped<EvaluationContextBuilder>();

        // Action handlers — one class each, discovered by scan.
        services.Scan(scan => scan
            .FromAssemblyOf<TagSyncActionHandler>()
            .AddClasses(c => c.AssignableTo<IActionHandler>(), publicOnly: false)
            .AsImplementedInterfaces()
            .WithScopedLifetime());
        services.AddScoped<ActionRegistry>();

        // Shared per-instance torrent snapshot + the cooldown map are process-wide, hence singletons;
        // the runner is scoped because it uses a scoped AppSettingStore / AppDbContext.
        services.AddSingleton<TorrentSnapshotCache>();
        services.AddSingleton<SourceCacheInvalidator>();
        services.AddSingleton<RuleCooldownTracker>();
        services.AddScoped<IRuleEngineRunner, RuleEngineRunner>();
        services.TryAddSingleton<IRunLogPublisher, NullRunLogPublisher>();

        // Named HTTP clients for the media adapters.
        services.AddHttpClient("plex", c => c.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            });
        services.AddHttpClient("jellyfin", c => c.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            });

        // One factory for every source kind.
        services.AddSingleton<SourceAdapterFactory>();
        services.AddSingleton<ISourceAdapterFactory>(sp => sp.GetRequiredService<SourceAdapterFactory>());
        services.AddSingleton<IQbtGatewayFactory>(sp => sp.GetRequiredService<SourceAdapterFactory>());

        services.AddSingleton<RuleEngineService>();
        services.AddSingleton<SourceHealthService>();
        services.AddSingleton<PathDiagnosticsService>();

        return services;
    }
}
