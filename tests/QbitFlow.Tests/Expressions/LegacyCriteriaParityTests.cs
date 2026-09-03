using FluentAssertions;
using QbitFlow.Core.Expressions;

namespace QbitFlow.Tests.Expressions;

/// <summary>
/// Golden test: the exact criteria strings shipped in the legacy <c>exampleConfig.json</c> must
/// evaluate the way the old <c>AutoTorrentRuleBase.Evaluate</c> did against a representative torrent.
/// </summary>
public class LegacyCriteriaParityTests
{
    private readonly CriteriaEvaluator _eval = new();

    // A "Movies" torrent: 3 GiB, added 2 years ago, last active 45 days ago, on a nearly-full S01 drive.
    private static Dictionary<string, object?> Context() => new()
    {
        ["Size"] = 3L * 1024 * 1024 * 1024,
        ["Name"] = "The.Big.Movie.2022.1080p.BluRay.x264-GRP",
        ["Category"] = "Movies",
        ["SavePath"] = "/media/jgarza/S01/Torrents/Movies",
        ["ContentPath"] = "/media/jgarza/S01/Torrents/Movies/The.Big.Movie.mkv",
        ["Progress"] = 1.0,
        ["AddedOn"] = DateTimeOffset.UtcNow.AddDays(-730).ToString("o"),
        ["LastActivityTime"] = DateTimeOffset.UtcNow.AddDays(-45).ToString("o"),
        ["ActiveTime"] = TimeSpan.FromDays(20).Ticks,
        ["plex_viewCount"] = 0,
        ["/media/jgarza/S01_FreeSizeGB"] = 0.4,
    };

    [Theory]
    [InlineData("(<Size> < 1073741824)", false)]                                              // Tag_SmallFile
    [InlineData("(<Size> >= 1073741824) && (<Size> < 10737418240)", true)]                    // Tag_MediumFile
    [InlineData("(<Size> >= 10737418240)", false)]                                            // Tag_LargeFile
    [InlineData("daysAgo(\"<LastActivityTime>\") >= 30.0", true)]                             // Tag_Inactive_30d
    [InlineData("daysAgo(\"<LastActivityTime>\") >= 90.0", false)]                            // Tag_Inactive_90d
    [InlineData("contains(\"<Category>\", \"Movies\") && daysAgo(\"<AddedOn>\") >= 365.0", true)] // Tag_OldMovie
    [InlineData("match(\"<Name>\", \"S[0-9][0-9]E[0-9][0-9]\")", false)]                       // Tag_TvShow (a movie)
    [InlineData("match(\"<Name>\", \"(720p|1080p|2160p|BluRay)\")", true)]                     // Category_Movies_ByQuality
    [InlineData("contains(\"<SavePath>\", \"/Music/\")", false)]                               // Tag_Music_ByPath
    [InlineData("(<plex_viewCount> == 0)", true)]                                              // Tag_NoViews_OnPlex
    [InlineData("match(\"<Category>\",\"(Shows|Movies)\")", true)]                             // Speed_Unlimited_ShowsMovies
    [InlineData("(<ActiveTime>/864000000000 >= 14.0)", true)]                                  // AutoMove active-time gate (ticks/day)
    [InlineData("(</media/jgarza/S01_FreeSizeGB> < 1.0) && ( match(\"<SavePath>\",\"S01\") )", true)] // Slow_Down_For_DriveFull_S01
    public void Example_config_criteria_evaluate_as_expected(string criteria, bool expected)
    {
        _eval.Evaluate(criteria, Context()).Should().Be(expected, criteria);
    }
}
