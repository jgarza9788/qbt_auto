using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Sources.Cache;
using Xunit;

namespace Qbitflow.Tests.Sources;

public class InMemorySourceDataCacheTests
{
    [Fact]
    public void TryGet_ReturnsFalse_WhenNothingCached()
    {
        var cache = new InMemorySourceDataCache();
        Assert.False(cache.TryGet(1, TimeSpan.FromMinutes(5), out _));
    }

    [Fact]
    public void TryGet_ReturnsTrue_WithinTtl()
    {
        var cache = new InMemorySourceDataCache();
        var data = new SourceFetchResult();
        cache.Set(1, data);

        Assert.True(cache.TryGet(1, TimeSpan.FromMinutes(5), out var result));
        Assert.Same(data, result);
    }

    [Fact]
    public void TryGet_ReturnsFalse_AfterInvalidate()
    {
        var cache = new InMemorySourceDataCache();
        cache.Set(1, new SourceFetchResult());
        cache.Invalidate(1);

        Assert.False(cache.TryGet(1, TimeSpan.FromMinutes(5), out _));
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenTtlAlreadyElapsed()
    {
        var cache = new InMemorySourceDataCache();
        cache.Set(1, new SourceFetchResult());

        Thread.Sleep(10);
        Assert.False(cache.TryGet(1, TimeSpan.Zero, out _));
    }

    [Fact]
    public void InvalidateAll_ClearsEverything()
    {
        var cache = new InMemorySourceDataCache();
        cache.Set(1, new SourceFetchResult());
        cache.Set(2, new SourceFetchResult());

        cache.InvalidateAll();

        Assert.False(cache.TryGet(1, TimeSpan.FromMinutes(5), out _));
        Assert.False(cache.TryGet(2, TimeSpan.FromMinutes(5), out _));
    }
}
