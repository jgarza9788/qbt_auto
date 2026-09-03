using FluentAssertions;
using QbitFlow.Core.Contracts;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Matching;

namespace QbitFlow.Tests.Matching;

public class FilenameMediaMatcherTests
{
    private static MediaItem Item(string title, int? year, string type, params (string path, long? size)[] files)
    {
        var m = new MediaItem { Title = title, Year = year, MediaType = type };
        foreach (var (p, s) in files)
            m.Files.Add(new MediaFilePath { Path = p, FileName = System.IO.Path.GetFileName(p), SizeBytes = s });
        return m;
    }

    private static readonly MediaCatalog Catalog = MediaCatalog.Build(
    [
        Item("The Matrix", 1999, "movie", ("/plex/Movies/The Matrix (1999)/The.Matrix.1999.1080p.BluRay.mkv", 8_000_000_000)),
        Item("The Office", 2005, "show",
            ("/plex/TV/The Office/S02E01.mkv", 350_000_000),
            ("/plex/TV/The Office/S02E02.mkv", 355_000_000)),
        Item("Dune", 2021, "movie", ("/plex/Movies/Dune/Dune.2021.2160p.mkv", null)),
    ]);

    private static readonly FilenameMediaMatcher Matcher = new();

    private static TorrentView Torrent(string name, string contentPath = "", string savePath = "", long size = 0) =>
        new() { Hash = "h", Name = name, ContentPath = contentPath, SavePath = savePath, Size = size };

    [Fact]
    public void Exact_filename_match_wins_with_confidence_1()
    {
        var t = Torrent("The.Matrix.1999.1080p.BluRay.x264-GRP",
            contentPath: "/downloads/The.Matrix.1999.1080p.BluRay.x264-GRP/The.Matrix.1999.1080p.BluRay.mkv");

        var match = Matcher.Match(t, Catalog);

        match.Should().NotBeNull();
        match!.Strategy.Should().Be("filename");
        match.Confidence.Should().Be(1.0);
        match.MediaItemId.Should().Be(Catalog.All.Single(e => e.Title == "The Matrix").MediaItemId);
    }

    [Fact]
    public void Path_segment_match_when_the_filename_differs()
    {
        var t = Torrent("The Office S02", contentPath: "/downloads/The Office/random-name.mkv");

        var match = Matcher.Match(t, Catalog);

        match.Should().NotBeNull();
        match!.Strategy.Should().Be("path-segment");
        match.MediaItemId.Should().Be(Catalog.All.Single(e => e.Title == "The Office").MediaItemId);
    }

    [Fact]
    public void Title_year_match_when_only_the_name_and_year_are_known()
    {
        // Filename/folder don't line up with the library; title + year do.
        var t = Torrent("The Office 2005", contentPath: "/dl/misc/blob.mkv");

        var match = Matcher.Match(t, Catalog);

        match.Should().NotBeNull();
        match!.Strategy.Should().Be("title-year");
        match.Confidence.Should().BeGreaterThanOrEqualTo(0.6);
        match.MediaItemId.Should().Be(Catalog.All.Single(e => e.Title == "The Office").MediaItemId);
    }

    [Fact]
    public void No_match_returns_null()
    {
        Matcher.Match(Torrent("Totally Unrelated Linux ISO"), Catalog).Should().BeNull();
    }
}
