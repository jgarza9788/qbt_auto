using Microsoft.Extensions.DependencyInjection;
using Qbitflow.Core.Interfaces;
using Qbitflow.Sources.Adapters;
using Qbitflow.Sources.Cache;
using Qbitflow.Sources.Concurrency;
using Qbitflow.Sources.Coordination;
using Qbitflow.Sources.Http;
using Qbitflow.Sources.Storage;

namespace Qbitflow.Sources;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything in this project: named HTTP clients, adapters, the cache,
    /// the per-host concurrency limiter, and the refresh coordinator. Callers must
    /// separately register an IParallelismSettingsProvider (Qbitflow.Infrastructure owns
    /// that implementation, since it reads AppSettings from the database).
    /// </summary>
    public static IServiceCollection AddQbitflowSources(this IServiceCollection services)
    {
        services.AddHttpClient(InstanceHttpClientFactory.SecureClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });

        services.AddHttpClient(InstanceHttpClientFactory.InsecureClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                UseCookies = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });

        services.AddSingleton<IInstanceHttpClientFactory, InstanceHttpClientFactory>();
        services.AddSingleton<IHostConcurrencyLimiter, HostConcurrencyLimiter>();
        services.AddSingleton<ISourceDataCache, InMemorySourceDataCache>();
        services.AddSingleton<SourceCacheOptions>();
        services.AddSingleton<IStorageUsageService, StorageUsageService>();

        services.AddSingleton<QbtAdapter>();
        services.AddSingleton<ISourceAdapter>(sp => sp.GetRequiredService<QbtAdapter>());
        services.AddSingleton<IQbtTorrentFilesProvider>(sp => sp.GetRequiredService<QbtAdapter>());
        services.AddSingleton<IQbtActionClient>(sp => sp.GetRequiredService<QbtAdapter>());
        services.AddSingleton<ISourceAdapter, PlexAdapter>();
        services.AddSingleton<ISourceAdapter, JellyfinAdapter>();
        services.AddSingleton<ISourceAdapter, TautulliAdapter>();
        services.AddSingleton<ISourceAdapter, JellystatAdapter>();
        services.AddSingleton<ISourceAdapter, JellyglanceAdapter>();

        services.AddSingleton<ISourceAdapterResolver, SourceAdapterResolver>();
        services.AddSingleton<ISourceRefreshCoordinator, SourceRefreshCoordinator>();

        return services;
    }
}
