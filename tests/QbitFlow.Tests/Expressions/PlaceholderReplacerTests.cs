using FluentAssertions;
using QbitFlow.Core.Expressions;

namespace QbitFlow.Tests.Expressions;

public class PlaceholderReplacerTests
{
    private static readonly Dictionary<string, object?> Ctx = new()
    {
        ["Name"] = "The.Show.S01E02",
        ["Size"] = 1073741824L,
        ["Tags"] = new[] { "hd", "seed" },
        ["Done"] = true,
    };

    [Fact]
    public void Substitutes_scalars_lists_and_bools()
    {
        PlaceholderReplacer.Apply("<Name>", Ctx).Should().Be("The.Show.S01E02");
        PlaceholderReplacer.Apply("<Size>", Ctx).Should().Be("1073741824");
        PlaceholderReplacer.Apply("<Tags>", Ctx).Should().Be("hd,seed");
        PlaceholderReplacer.Apply("<Done>", Ctx).Should().Be("True");
    }

    [Fact]
    public void Unknown_key_becomes_an_error_marker()
    {
        PlaceholderReplacer.Apply("<Nope>", Ctx).Should().Contain("ERROR").And.Contain("Nope");
    }

    [Fact]
    public void Leaves_text_without_tokens_untouched()
    {
        PlaceholderReplacer.Apply("no tokens here", Ctx).Should().Be("no tokens here");
    }

    [Fact]
    public void ReferencedKeys_finds_every_token()
    {
        PlaceholderReplacer.ReferencedKeys("(<A> && <B>) || <A>")
            .Should().BeEquivalentTo("A", "B");
    }
}
