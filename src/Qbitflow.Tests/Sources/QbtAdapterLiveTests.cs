using Microsoft.Extensions.DependencyInjection;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Sources;
using Qbitflow.Sources.Adapters;
using Xunit;
using Xunit.Abstractions;

namespace Qbitflow.Tests.Sources;

/// <summary>
/// End-to-end check against a real qBittorrent WebUI. Skipped unless QBT_LIVE_URL is set, so it
/// is a no-op in CI / normal `dotnet test` runs. Run it with:
///
///   QBT_LIVE_URL=http://host:8080 QBT_LIVE_USER=admin QBT_LIVE_PASS=secret \
///     dotnet test --filter "FullyQualifiedName~QbtAdapterLiveTests"
///
/// Set QBT_LIVE_VERIFY_SSL=false for a self-signed https endpoint.
/// </summary>
[Trait("Category", "Live")]
public class QbtAdapterLiveTests(ITestOutputHelper output)
{
    private readonly record struct LiveCfg(string Url, string? User, string? Pass, bool VerifySsl);

    private static LiveCfg? Config()
    {
        var url = Environment.GetEnvironmentVariable("QBT_LIVE_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var verify = Environment.GetEnvironmentVariable("QBT_LIVE_VERIFY_SSL");
        return new LiveCfg(
            url,
            Environment.GetEnvironmentVariable("QBT_LIVE_USER"),
            Environment.GetEnvironmentVariable("QBT_LIVE_PASS"),
            !string.Equals(verify, "false", StringComparison.OrdinalIgnoreCase));
    }

    private static QbtAdapter BuildAdapter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQbitflowSources();
        return services.BuildServiceProvider().GetRequiredService<QbtAdapter>();
    }

    private static SourceConnectionInfo Connection(LiveCfg cfg) => new()
    {
        InstanceId = 1,
        InstanceName = "live",
        SourceType = SourceType.Qbittorrent,
        BaseUrl = cfg.Url,
        Username = cfg.User,
        Password = cfg.Pass,
        TimeoutSeconds = 15,
        VerifySsl = cfg.VerifySsl
    };

    [Fact]
    public async Task TestConnection_Succeeds_AgainstRealQbittorrent()
    {
        var cfg = Config();
        if (cfg is null)
        {
            output.WriteLine("SKIPPED: set QBT_LIVE_URL (+ QBT_LIVE_USER / QBT_LIVE_PASS) to run the live qBittorrent test.");
            return;
        }

        var result = await BuildAdapter().TestConnectionAsync(Connection(cfg!.Value));

        output.WriteLine($"Success={result.Success}  Duration={result.Duration.TotalMilliseconds:0}ms");
        output.WriteLine($"Message={result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Contains("qBittorrent", result.Message ?? "");
    }

    [Fact]
    public async Task Fetch_ReturnsTorrents_AgainstRealQbittorrent()
    {
        var cfg = Config();
        if (cfg is null)
        {
            output.WriteLine("SKIPPED: set QBT_LIVE_URL (+ QBT_LIVE_USER / QBT_LIVE_PASS) to run the live qBittorrent test.");
            return;
        }

        var result = await BuildAdapter().FetchAsync(Connection(cfg!.Value));

        output.WriteLine($"Fetched {result.Torrents.Count} torrent(s).");
        foreach (var t in result.Torrents.Take(5))
        {
            var hash = t.Hash.Length >= 8 ? t.Hash[..8] : t.Hash;
            output.WriteLine($"  {hash}  {t.State,-12}  {t.Name}");
        }

        Assert.NotNull(result.Torrents);
    }
}
