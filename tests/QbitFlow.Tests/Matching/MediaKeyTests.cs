using FluentAssertions;
using QbitFlow.Core.Matching;

namespace QbitFlow.Tests.Matching;

public class MediaKeyTests
{
    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", "the matrix 1999")]
    [InlineData("Some.Show.S01E02.HDTV.x265.mkv", "some show s01e02")]
    [InlineData("Plain Title.mkv", "plain title")]
    public void NormalizeFileName_strips_tags_and_separators(string input, string expected)
    {
        MediaKey.NormalizeFileName(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeFileName_drops_bracketed_junk_but_keeps_year_parens()
    {
        MediaKey.NormalizeFileName("Album - Artist (2020) [FLAC].flac").Should().Be("album artist (2020)");
    }

    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.mkv", "the matrix")]
    [InlineData("Some.Show.S01E02.1080p.mkv", "some show")]
    [InlineData("Plain Title", "plain title")]
    public void NormalizeTitle_drops_year_and_episode_suffix(string input, string expected)
    {
        MediaKey.NormalizeTitle(input).Should().Be(expected);
    }

    [Fact]
    public void ExtractTitleYear_pulls_a_trailing_year()
    {
        var (title, year) = MediaKey.ExtractTitleYear("The.Godfather.1972.2160p.UHD.BluRay.mkv");
        title.Should().Be("the godfather");
        year.Should().Be(1972);
    }
}
