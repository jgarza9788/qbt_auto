using FluentAssertions;
using QbitFlow.Core.Domain;
using QbitFlow.Sources.Plex;

namespace QbitFlow.Tests.Sources;

public class PlexAdapterTests
{
    private const string Sections = """
        <MediaContainer>
          <Directory key="1" type="movie" title="Movies"/>
          <Directory key="2" type="show" title="TV"/>
          <Directory key="3" type="artist" title="Music"/>
        </MediaContainer>
        """;

    private const string MoviesAll = """
        <MediaContainer>
          <Video type="movie" title="The Film" year="2024" rating="7.8" ratingKey="100" duration="7200000">
            <Genre tag="Action"/><Genre tag="Sci-Fi"/>
            <Media><Part file="/data/Movies/The Film (2024)/film.mkv"/></Media>
          </Video>
        </MediaContainer>
        """;

    private const string ShowsAll = """
        <MediaContainer>
          <Directory type="show" title="The Show" year="2020" ratingKey="200"/>
        </MediaContainer>
        """;

    private const string ShowLeaves = """
        <MediaContainer>
          <Video type="episode" grandparentTitle="The Show">
            <Media><Part file="/data/TV/The Show/S01E01.mkv"/></Media>
          </Video>
        </MediaContainer>
        """;

    private const string History = """
        <MediaContainer>
          <Video type="movie" title="The Film" viewedAt="1900000000"/>
          <Video type="episode" grandparentTitle="The Show" viewedAt="1900000100"/>
          <Video type="episode" grandparentTitle="The Show" viewedAt="1900000200"/>
          <Video type="movie" title="Old Movie" viewedAt="1"/>
        </MediaContainer>
        """;

    private static PlexAdapter Build(StubHttpHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("http://plex.local:32400") },
            new PlexConnectionInfo(Guid.NewGuid(), new Uri("http://plex.local:32400"),
                SourceAuthMode.PlexToken, "", "token123", "qbit-flow"));

    [Fact]
    public async Task FetchMedia_reads_movies_and_show_leaves()
    {
        var h = new StubHttpHandler()
            .Add("/library/sections/1/all", MoviesAll)
            .Add("/library/sections/2/all", ShowsAll)
            .Add("/library/metadata/200/allLeaves", ShowLeaves)
            .Add("/library/sections", Sections);

        var media = await Build(h).FetchMediaAsync(CancellationToken.None);

        var film = media.Single(m => m.Title == "The Film");
        film.MediaType.Should().Be("movie");
        film.Year.Should().Be(2024);
        film.Genres.Should().BeEquivalentTo("Action", "Sci-Fi");
        film.Files.Single().FileName.Should().Be("film.mkv");

        var show = media.Single(m => m.Title == "The Show");
        show.MediaType.Should().Be("show");
        show.Files.Single().Path.Should().EndWith("S01E01.mkv");
    }

    [Fact]
    public async Task FetchWatch_groups_by_title_filters_by_since_and_takes_the_latest_view()
    {
        var h = new StubHttpHandler().Add("/status/sessions/history/all", History);

        var since = DateTimeOffset.FromUnixTimeSeconds(1_000_000_000);
        var watch = await Build(h).FetchWatchAsync(since, CancellationToken.None);

        watch.Should().HaveCount(2);   // "Old Movie" is before `since`
        var show = watch.Single(w => w.Title == "The Show");
        show.MediaType.Should().Be("show");
        show.PlayCount.Should().Be(2);
        show.LastPlayedUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1900000200));
    }

    [Fact]
    public async Task Test_pings_library_sections()
    {
        var h = new StubHttpHandler().Add("/library/sections", Sections);
        (await Build(h).TestAsync(CancellationToken.None)).Ok.Should().BeTrue();
    }
}
