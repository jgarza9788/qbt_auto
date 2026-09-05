using Qbitflow.Core.Domain;
using Qbitflow.Snapshot;
using Xunit;

namespace Qbitflow.Tests.Snapshot;

public class PathKeyNormalizerTests
{
    [Fact]
    public void Normalize_ReturnsNull_ForNullOrEmptyInput()
    {
        Assert.Null(PathKeyNormalizer.Normalize(null, []));
        Assert.Null(PathKeyNormalizer.Normalize("", []));
        Assert.Null(PathKeyNormalizer.Normalize("   ", []));
    }

    [Fact]
    public void Normalize_LowercasesAndConvertsBackslashes_WithNoRules()
    {
        var result = PathKeyNormalizer.Normalize(@"C:\Downloads\Foo\Bar.mkv", []);
        Assert.Equal("c:/downloads/foo/bar.mkv", result);
    }

    [Fact]
    public void Normalize_TrimsTrailingSlash()
    {
        var result = PathKeyNormalizer.Normalize("/downloads/foo/", []);
        Assert.Equal("/downloads/foo", result);
    }

    [Fact]
    public void Normalize_AppliesMatchingRule_RewritingPrefix()
    {
        var rules = new List<PathMappingRule>
        {
            new() { SourcePrefix = "/downloads", CanonicalPrefix = "/media/downloads" }
        };

        var result = PathKeyNormalizer.Normalize("/downloads/Foo.2020/Foo.mkv", rules);

        Assert.Equal("/media/downloads/foo.2020/foo.mkv", result);
    }

    [Fact]
    public void Normalize_MakesQbtAndPlexPathsCollapseToSameKey()
    {
        var rules = new List<PathMappingRule>
        {
            new() { SourcePrefix = "/downloads", CanonicalPrefix = "/media" }
        };

        var qbtPath = PathKeyNormalizer.Normalize("/downloads/Foo.2020/Foo.mkv", rules);
        var plexPath = PathKeyNormalizer.Normalize("/media/Foo.2020/Foo.mkv", rules);

        Assert.Equal(qbtPath, plexPath);
    }

    [Fact]
    public void Normalize_SkipsDisabledRules()
    {
        var rules = new List<PathMappingRule>
        {
            new() { SourcePrefix = "/downloads", CanonicalPrefix = "/media", Enabled = false }
        };

        var result = PathKeyNormalizer.Normalize("/downloads/foo.mkv", rules);

        Assert.Equal("/downloads/foo.mkv", result);
    }

    [Fact]
    public void Normalize_FirstMatchingRuleWins()
    {
        var rules = new List<PathMappingRule>
        {
            new() { SourcePrefix = "/downloads", CanonicalPrefix = "/first" },
            new() { SourcePrefix = "/downloads", CanonicalPrefix = "/second" }
        };

        var result = PathKeyNormalizer.Normalize("/downloads/foo.mkv", rules);

        Assert.Equal("/first/foo.mkv", result);
    }
}
