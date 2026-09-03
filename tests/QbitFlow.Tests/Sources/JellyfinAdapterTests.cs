using FluentAssertions;
using QbitFlow.Sources.Jellyfin;

namespace QbitFlow.Tests.Sources;

public class JellyfinAdapterTests
{
    private const string ItemsCatalog = """
        { "Items": [
          { "Id": "m1", "Name": "The Film", "Type": "Movie", "ProductionYear": 2024,
            "CommunityRating": 7.8, "Genres": ["Action","Sci-Fi"], "RunTimeTicks": 72000000000,
            "Path": "/media/Movies/The Film (2024)/film.mkv" },
          { "Id": "e1", "Name": "Ep 1", "SeriesName": "The Show", "Type": "Episode",
            "Path": "/media/TV/The Show/S01E01.mkv" }
        ] }
        """;

    private const string Users = """[ { "Id": "u1", "Name": "alice" }, { "Id": "u2", "Name": "bob" } ]""";

    private const string AliceItems = """
        { "Items": [
          { "Id": "m1", "Name": "The Film", "Type": "Movie",
            "UserData": { "PlayCount": 2, "LastPlayedDate": "2026-02-01T00:00:00Z" } },
          { "Id": "e1", "Name": "Ep 1", "SeriesName": "The Show", "Type": "Episode",
            "UserData": { "PlayCount": 3, "LastPlayedDate": "2026-03-01T00:00:00Z" } }
        ] }
        """;

    private const string BobItems = """
        { "Items": [
          { "Id": "e2", "Name": "Ep 2", "SeriesName": "The Show", "Type": "Episode",
            "UserData": { "PlayCount": 1, "LastPlayedDate": "2026-04-01T00:00:00Z" } }
        ] }
        """;

    private static JellyfinAdapter Build(StubHttpHandler handler, string userScope = "all") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://jf.local") },
            new JellyfinConnectionInfo(Guid.NewGuid(), new Uri("http://jf.local"), "key", userScope));

    [Fact]
    public async Task FetchMedia_maps_types_genres_and_duration()
    {
        var h = new StubHttpHandler().Add("/Items?Recursive=true&IncludeItemTypes=Movie,Series,Episode", ItemsCatalog);
        var media = await Build(h).FetchMediaAsync(CancellationToken.None);

        media.Should().HaveCount(2);
        var film = media.Single(m => m.Title == "The Film");
        film.MediaType.Should().Be("movie");
        film.Year.Should().Be(2024);
        film.Genres.Should().BeEquivalentTo("Action", "Sci-Fi");
        film.DurationMs.Should().Be(7_200_000);
        film.Files.Should().ContainSingle();

        // Episode items keep their per-file granularity (type "episode") but are titled by their series.
        var episode = media.Single(m => m.Title == "The Show");
        episode.MediaType.Should().Be("episode");
        episode.Files.Single().Path.Should().EndWith("S01E01.mkv");
    }

    [Fact]
    public async Task FetchWatch_aggregates_play_counts_across_users_and_groups_episodes_by_series()
    {
        var h = new StubHttpHandler()
            .Add("/Users/u1/Items", AliceItems)
            .Add("/Users/u2/Items", BobItems)
            .Add("/Users", Users);   // least specific last

        var watch = await Build(h).FetchWatchAsync(DateTimeOffset.UnixEpoch, CancellationToken.None);

        var show = watch.Single(w => w.Title == "The Show");
        show.MediaType.Should().Be("show");
        show.PlayCount.Should().Be(4);   // alice 3 + bob 1
        show.LastPlayedUtc.Should().Be(DateTimeOffset.Parse("2026-04-01T00:00:00Z"));

        watch.Single(w => w.Title == "The Film").PlayCount.Should().Be(2);
    }

    [Fact]
    public async Task FetchWatch_honours_a_named_user_scope()
    {
        var h = new StubHttpHandler()
            .Add("/Users/u1/Items", AliceItems)
            .Add("/Users/u2/Items", BobItems)
            .Add("/Users", Users);

        var watch = await Build(h, userScope: "bob").FetchWatchAsync(DateTimeOffset.UnixEpoch, CancellationToken.None);

        watch.Should().ContainSingle().Which.Title.Should().Be("The Show");
        watch.Single().PlayCount.Should().Be(1);
    }

    [Fact]
    public async Task Test_hits_system_info()
    {
        var h = new StubHttpHandler().Add("/System/Info", """{ "Version": "10.9.0" }""");
        (await Build(h).TestAsync(CancellationToken.None)).Ok.Should().BeTrue();
    }
}
