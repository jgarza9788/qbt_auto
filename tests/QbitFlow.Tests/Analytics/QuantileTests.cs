using FluentAssertions;
using QbitFlow.Engine.Analytics;

namespace QbitFlow.Tests.Analytics;

public class QuantileTests
{
    [Fact]
    public void Empty_returns_empty()
    {
        Quantile.NormalizeQuantile(new Dictionary<string, double>()).Should().BeEmpty();
    }

    [Fact]
    public void Single_item_scores_one()
    {
        Quantile.NormalizeQuantile(new Dictionary<string, double> { ["a"] = 42 })
            .Should().ContainSingle().Which.Score.Should().Be(1.0);
    }

    [Fact]
    public void Highest_value_scores_one_lowest_scores_zero()
    {
        var result = Quantile.NormalizeQuantile(new Dictionary<string, double>
        {
            ["cold"] = 0, ["warm"] = 5, ["hot"] = 100,
        }).ToDictionary(x => x.Key, x => x.Score);

        result["cold"].Should().Be(0.0);
        result["hot"].Should().Be(1.0);
        result["warm"].Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Ties_share_the_mid_rank()
    {
        var result = Quantile.NormalizeQuantile(new Dictionary<string, double>
        {
            ["a"] = 1, ["b"] = 1, ["c"] = 1, ["d"] = 9,
        }).ToDictionary(x => x.Key, x => x.Score);

        // ranks 0,1,2 tie → mid-rank 1 → 1/3; "d" at rank 3 → 1.0
        result["a"].Should().BeApproximately(1.0 / 3, 1e-9);
        result["b"].Should().BeApproximately(1.0 / 3, 1e-9);
        result["c"].Should().BeApproximately(1.0 / 3, 1e-9);
        result["d"].Should().Be(1.0);
    }
}
