using FluentAssertions;
using QbitFlow.Core.Expressions;

namespace QbitFlow.Tests.Expressions;

public class CriteriaEvaluatorTests
{
    private readonly CriteriaEvaluator _eval = new();

    private static Dictionary<string, object?> Ctx(params (string, object?)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void Numeric_comparison()
    {
        _eval.Evaluate("<Size> < 1073741824", Ctx(("Size", 500L))).Should().BeTrue();
        _eval.Evaluate("<Size> < 1073741824", Ctx(("Size", 2_000_000_000L))).Should().BeFalse();
    }

    [Fact]
    public void Contains_and_match_helpers()
    {
        var ctx = Ctx(("Name", "The.Show.S01E02.1080p"), ("SavePath", "/media/Music/x"));
        _eval.Evaluate("contains(\"<SavePath>\", \"/Music/\")", ctx).Should().BeTrue();
        _eval.Evaluate("match(\"<Name>\", \"S[0-9][0-9]E[0-9][0-9]\")", ctx).Should().BeTrue();
        _eval.Evaluate("match(\"<Name>\", \"^music$\")", ctx).Should().BeFalse();
    }

    [Fact]
    public void DaysAgo_helper()
    {
        var iso = DateTimeOffset.UtcNow.AddDays(-40).ToString("o");
        _eval.Evaluate("daysAgo(\"<When>\") >= 30.0", Ctx(("When", iso))).Should().BeTrue();
    }

    [Fact]
    public void Malformed_expression_returns_null()
    {
        _eval.Evaluate("<Size> < ", Ctx(("Size", 1L))).Should().BeNull();
        _eval.Evaluate("this is not code", Ctx()).Should().BeNull();
    }

    [Fact]
    public void Unknown_field_marker_fails_to_null()
    {
        _eval.Evaluate("<Missing> == 1", Ctx()).Should().BeNull();
    }

    [Fact]
    public void Validate_flags_bad_expressions()
    {
        _eval.Validate("<Size> >= 1073741824").Ok.Should().BeTrue();
        _eval.Validate("<Size> >= ").Ok.Should().BeFalse();
    }
}
