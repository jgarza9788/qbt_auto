using Qbitflow.Core.Domain;
using Qbitflow.Sources.Storage;
using Xunit;

namespace Qbitflow.Tests.Sources;

public class StorageUsageServiceTests
{
    [Fact]
    public void GetUsage_ReturnsUnavailable_ForNonExistentPath()
    {
        var service = new StorageUsageService();
        var config = new StoragePathConfig { Id = 1, Name = "ghost", Path = "Z:\\this\\path\\does\\not\\exist\\qbitflow-test" };

        var result = service.GetUsage(config);

        Assert.False(result.Available);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void GetUsage_ReturnsUsage_ForExistingPath()
    {
        var service = new StorageUsageService();
        var config = new StoragePathConfig { Id = 2, Name = "temp", Path = Path.GetTempPath() };

        var result = service.GetUsage(config);

        Assert.True(result.Available);
        Assert.True(result.TotalBytes > 0);
        Assert.True(result.FreeBytes >= 0);
    }

    [Fact]
    public async Task GetOrComputeFolderSizeAsync_ReturnsNull_ForNonExistentPath()
    {
        var service = new StorageUsageService();
        var config = new StoragePathConfig { Id = 3, Name = "ghost", Path = "Z:\\nope\\nope\\qbitflow-test", FolderSizeScanIntervalMinutes = 60 };

        var size = await service.GetOrComputeFolderSizeAsync(config);

        Assert.Null(size);
    }

    [Fact]
    public async Task GetOrComputeFolderSizeAsync_ComputesAndCaches()
    {
        var service = new StorageUsageService();
        var dir = Directory.CreateTempSubdirectory("qbitflow-test-");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir.FullName, "a.bin"), new byte[1000]);
            var config = new StoragePathConfig { Id = 4, Name = "scratch", Path = dir.FullName, FolderSizeScanIntervalMinutes = 60 };

            var size = await service.GetOrComputeFolderSizeAsync(config);
            Assert.Equal(1000, size);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
