using FluentAssertions;
using QbitFlow.Web.Pages;

namespace QbitFlow.Tests.Pages;

public class CpuSpeedMapTests
{
    [Theory]
    [InlineData(1, "Low")]
    [InlineData(2, "Low")]
    [InlineData(7, "Medium")]
    [InlineData(8, "Medium")]
    [InlineData(20, "High")]
    [InlineData(100, "SuperHigh")]
    public void ToLabel_picks_the_nearest_tier(int value, string expected) =>
        CpuSpeedMap.ToLabel(value).Should().Be(expected);

    [Theory]
    [InlineData("Low", 2)]
    [InlineData("Medium", 8)]
    [InlineData("High", 16)]
    [InlineData("SuperHigh", 32)]
    [InlineData("superhigh", 32)]
    [InlineData("bogus", 8)]
    [InlineData(null, 8)]
    public void ToValue_matches_labels_case_insensitively_and_defaults(string? label, int expected) =>
        CpuSpeedMap.ToValue(label).Should().Be(expected);
}
