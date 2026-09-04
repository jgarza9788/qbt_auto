using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;
using QbitFlow.Core.Domain;
using QbitFlow.Engine;
using QbitFlow.Engine.Analytics;
using QbitFlow.Infrastructure;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Tests.Analytics;

public class AnalyticsServiceTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"qbitflow-an-{Guid.NewGuid():N}.db");
    private ServiceProvider _sp = null!;
    private readonly FakeFactory _factory = new();
    private Guid _plexId, _jfId, _qbtId;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQbitFlowInfrastructure($"Data Source={_dbPath}");
        services.AddQbitFlowEngine();
        services.RemoveAll<ISourceAdapterFactory>();
        services.RemoveAll<IQbtGatewayFactory>();
        services.AddSingleton<ISourceAdapterFactory>(_factory);
        services.AddSingleton<IQbtGatewayFactory>(_factory);
        _sp = services.BuildServiceProvider();

        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        var plex = new SourceConnection { Name = "plex", Kind = SourceKind.Plex, BaseUrl = "http://p" };
        var jf = new SourceConnection { Name = "jf", Kind = SourceKind.Jellyfin, BaseUrl = "http://j" };
        var qbt = new SourceConnection { Name = "qbt", Kind = SourceKind.Qbt, BaseUrl = "http://q" };
        db.SourceConnections.AddRange(plex, jf, qbt);
        await db.SaveChangesAsync();
        (_plexId, _jfId, _qbtId) = (plex.Id, jf.Id, qbt.Id);

        // Plex: two movies. Jellyfin: the SAME "The Matrix" (so watch counts aggregate) + one more.
        _factory.Media[_plexId] = new FakeMedia(
            media:
            [
                new MediaRecord("p1", "The Matrix", "movie", 1999, 8.7, ["Sci-Fi"], 8_160_000,
                    [new MediaFile("/plex/Movies/The Matrix (1999)/The.Matrix.1999.1080p.mkv", "The.Matrix.1999.1080p.mkv", 8_000_000_000)]),
                new MediaRecord("p2", "Old Cartoon", "movie", 1988, 6.0, [], 5_000_000,
                    [new MediaFile("/plex/Movies/Old Cartoon/old.cartoon.1988.mkv", "old.cartoon.1988.mkv", 700_000_000)]),
            ],
            watch:
            [
                new WatchRecord("p1", "The Matrix", "movie", 10, DateTimeOffset.UtcNow.AddDays(-3)),
                new WatchRecord("p2", "Old Cartoon", "movie", 0, null),
            ]);

        _factory.Media[_jfId] = new FakeMedia(
            media:
            [
                new MediaRecord("j1", "The Matrix", "movie", 1999, 8.7, ["Sci-Fi"], 8_160_000,
                    [new MediaFile("/jf/Movies/The Matrix/The.Matrix.1999.1080p.mkv", "The.Matrix.1999.1080p.mkv", 8_000_000_000)]),
                new MediaRecord("j2", "Fresh Film", "movie", 2025, 7.0, ["Drama"], 6_000_000,
                    [new MediaFile("/jf/Movies/Fresh Film/fresh.film.2025.mkv", "fresh.film.2025.mkv", 4_000_000_000)]),
            ],
            watch:
            [
                new WatchRecord("j1", "The Matrix", "movie", 5, DateTimeOffset.UtcNow.AddDays(-1)),
                new WatchRecord("j2", "Fresh Film", "movie", 8, DateTimeOffset.UtcNow.AddDays(-2)),
            ]);

        _factory.Torrents =
        [
            new TorrentView { Hash = "hm", Name = "The.Matrix.1999.1080p", Category = "Movies", Size = 8_000_000_000,
                ContentPath = "/dl/The.Matrix.1999.1080p/The.Matrix.1999.1080p.mkv" },
            new TorrentView { Hash = "hc", Name = "old.cartoon.1988", Category = "Movies", Size = 700_000_000,
                ContentPath = "/dl/old.cartoon.1988/old.cartoon.1988.mkv" },
            new TorrentView { Hash = "hf", Name = "fresh.film.2025", Category = "Movies", Size = 4_000_000_000,
                ContentPath = "/dl/fresh.film.2025/fresh.film.2025.mkv" },
            new TorrentView { Hash = "hx", Name = "Ubuntu.24.04.iso", Category = "Linux", Size = 3_000_000_000,
                ContentPath = "/dl/Ubuntu.24.04.iso" },
        ];
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Refresh_aggregates_watch_data_and_scores_torrents_per_category()
    {
        var svc = _sp.GetRequiredService<IAnalyticsService>();
        var result = await svc.RefreshAsync(CancellationToken.None);

        result.MediaSourcesOk.Should().Be(2);
        result.TorrentsScored.Should().Be(4);
        result.TorrentsMatched.Should().Be(3);   // matrix, cartoon, fresh film — not the ISO

        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // "The Matrix" merged across both sources → one MediaItem, two source stats.
        var matrix = await db.MediaItems.Include(m => m.SourceStats)
            .SingleAsync(m => m.Title == "The Matrix");
        matrix.SourceStats.Should().HaveCount(2);
        // weighted: (10 + 5) * 0.01 "all"-window weight
        matrix.WeightedWatchTotal.Should().BeApproximately(0.15, 1e-9);

        // Movies bucket: matrix (0.15) most watched, then fresh film (0.08), cartoon (0); ISO in its own bucket.
        var movies = await db.MediaScoreCache.Where(s => s.Category == "Movies").ToListAsync();
        movies.Should().HaveCount(3);
        movies.Single(s => s.TorrentHash == "hm").WatchPopularity.Should().Be(1.0);
        movies.Single(s => s.TorrentHash == "hc").WatchPopularity.Should().Be(0.0);
        movies.Single(s => s.TorrentHash == "hm").IsMediaMatched.Should().BeTrue();

        var linux = await db.MediaScoreCache.Where(s => s.Category == "Linux").ToListAsync();
        linux.Should().ContainSingle();
        linux[0].IsMediaMatched.Should().BeFalse();
        linux[0].WatchPopularity.Should().Be(1.0);   // only member of its bucket
        linux[0].WatchTotal.Should().Be(0);
    }

    [Fact]
    public async Task Cached_enricher_serves_the_score_to_the_evaluation_context()
    {
        await _sp.GetRequiredService<IAnalyticsService>().RefreshAsync(CancellationToken.None);

        var enricher = _sp.GetRequiredService<IMediaEnricher>();
        var fields = await enricher.EnrichAsync(_qbtId,
            new TorrentView { Hash = "hm", Name = "x" }, CancellationToken.None);

        fields["is_media_matched"].Should().Be(true);
        ((double)fields["watch_popularity"]!).Should().Be(1.0);
        ((double)fields["watch_total"]!).Should().BeApproximately(0.15, 1e-9);
        fields["media_title"].Should().Be("The Matrix");
        ((double)fields["plex_nview"]!).Should().Be(1.0);   // alias of watch_popularity

        var iso = await enricher.EnrichAsync(_qbtId,
            new TorrentView { Hash = "hx", Name = "x" }, CancellationToken.None);
        iso["is_media_matched"].Should().Be(false);
        ((double)iso["watch_popularity"]!).Should().Be(1.0);   // sole member of the Linux bucket
        ((double)iso["days_since_last_watched"]!).Should().Be(99999d);
    }

    [Fact]
    public async Task The_renamed_field_no_longer_answers_to_hotcold()
    {
        await _sp.GetRequiredService<IAnalyticsService>().RefreshAsync(CancellationToken.None);

        var fields = await _sp.GetRequiredService<IMediaEnricher>().EnrichAsync(_qbtId,
            new TorrentView { Hash = "hm", Name = "x" }, CancellationToken.None);

        fields.Should().ContainKey("watch_popularity").And.NotContainKey("hotcold");
        QbitFlow.Core.Expressions.FieldCatalog.ByKey.Should()
            .ContainKey("watch_popularity").And.NotContainKey("hotcold");
    }

    // ---- fakes ----

    private sealed class FakeMedia(IReadOnlyList<MediaRecord> media, IReadOnlyList<WatchRecord> watch) : IMediaSourceAdapter
    {
        public SourceKind Kind => SourceKind.Plex;
        public Guid SourceId => Guid.Empty;
        public Task<HealthResult> TestAsync(CancellationToken ct) => Task.FromResult(HealthResult.Healthy(1));
        public Task<IReadOnlyList<MediaRecord>> FetchMediaAsync(CancellationToken ct) => Task.FromResult(media);
        public Task<IReadOnlyList<WatchRecord>> FetchWatchAsync(DateTimeOffset since, CancellationToken ct) => Task.FromResult(watch);
    }

    private sealed class FakeFactory : ISourceAdapterFactory, IQbtGatewayFactory, IQbtAdapter
    {
        public Dictionary<Guid, IMediaSourceAdapter> Media { get; } = new();
        public IReadOnlyList<TorrentView> Torrents = [];

        public IMediaSourceAdapter GetMediaAdapter(Guid id) => Media[id];
        public IQbtAdapter GetQbtAdapter(Guid id) => this;
        public IQbtActionTarget GetQbtActionTarget(Guid id) => throw new NotSupportedException();
        public IQbtAdapter GetAdapter(Guid id) => this;
        public IQbtActionTarget GetActionTarget(Guid id) => throw new NotSupportedException();
        public void Invalidate(Guid id) { }

        public Guid SourceId => Guid.Empty;
        public Task<HealthResult> TestAsync(CancellationToken ct) => Task.FromResult(HealthResult.Healthy(1));
        public Task<IReadOnlyList<TorrentView>> FetchTorrentsAsync(CancellationToken ct) => Task.FromResult(Torrents);
    }
}
