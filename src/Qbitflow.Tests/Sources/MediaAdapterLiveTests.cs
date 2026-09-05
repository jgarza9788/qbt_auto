using Microsoft.Extensions.DependencyInjection;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Core.Interfaces;
using Qbitflow.Sources;
using Qbitflow.Sources.Adapters;
using Xunit;
using Xunit.Abstractions;

namespace Qbitflow.Tests.Sources;

/// <summary>
/// Opt-in end-to-end checks against real media-server WebUIs. Each test is a no-op unless its
/// <c>&lt;PREFIX&gt;_LIVE_URL</c> env var is set, so `dotnet test` stays offline by default. Run e.g.:
///
///   JF_LIVE_URL=https://jelly.example.com JF_LIVE_KEY=abcdef \
///     dotnet test --filter "FullyQualifiedName~MediaAdapterLiveTests"
///
/// Prefixes: JF (Jellyfin), PLEX, TAUTULLI, JELLYSTAT, JELLYGLANCE.
/// Add <c>&lt;PREFIX&gt;_LIVE_VERIFY_SSL=false</c> for a self-signed https endpoint.
/// </summary>
[Trait("Category", "Live")]
public class MediaAdapterLiveTests(ITestOutputHelper output)
{
    private static ISourceAdapter Adapter(SourceType type)
    {
        var sp = new ServiceCollection().AddLogging().AddQbitflowSources().BuildServiceProvider();
        return sp.GetRequiredService<IEnumerable<ISourceAdapter>>().First(a => a.SourceType == type);
    }

    private SourceConnectionInfo? Connection(string prefix, SourceType type)
    {
        var url = Environment.GetEnvironmentVariable($"{prefix}_LIVE_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            output.WriteLine($"SKIPPED: set {prefix}_LIVE_URL (+ {prefix}_LIVE_KEY) to run this test.");
            return null;
        }

        var verify = Environment.GetEnvironmentVariable($"{prefix}_LIVE_VERIFY_SSL");
        return new SourceConnectionInfo
        {
            InstanceId = 1,
            InstanceName = $"{prefix}-live",
            SourceType = type,
            BaseUrl = url,
            ApiKey = Environment.GetEnvironmentVariable($"{prefix}_LIVE_KEY"),
            TimeoutSeconds = 15,
            VerifySsl = !string.Equals(verify, "false", StringComparison.OrdinalIgnoreCase)
        };
    }

    private async Task RunAsync(string prefix, SourceType type)
    {
        var conn = Connection(prefix, type);
        if (conn is null)
        {
            return;
        }

        var adapter = Adapter(type);

        var result = await adapter.TestConnectionAsync(conn);
        output.WriteLine($"{type}: Success={result.Success}  {result.Duration.TotalMilliseconds:0}ms");
        output.WriteLine($"  {result.Message}");
        Assert.True(result.Success, result.Message);

        var fetched = await adapter.FetchAsync(conn);
        output.WriteLine($"  fetched: {fetched.MediaItems.Count} media item(s), {fetched.WatchHistory.Count} watch record(s)");
        foreach (var mi in fetched.MediaItems.Take(3))
        {
            output.WriteLine($"    {mi.MediaType,-8} {mi.Title}  [{string.Join(", ", mi.FilePaths)}]");
        }
    }

    [Fact] public Task Jellyfin() => RunAsync("JF", SourceType.Jellyfin);
    [Fact] public Task Plex() => RunAsync("PLEX", SourceType.Plex);
    [Fact] public Task Tautulli() => RunAsync("TAUTULLI", SourceType.Tautulli);
    [Fact] public Task Jellystat() => RunAsync("JELLYSTAT", SourceType.Jellystat);
    [Fact] public Task Jellyglance() => RunAsync("JELLYGLANCE", SourceType.Jellyglance);
}
