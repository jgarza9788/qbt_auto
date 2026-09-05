using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Sources.Storage;

public interface IStorageUsageService
{
    /// <summary>Never throws -- an unreachable/missing path comes back as Available=false with an Error message.</summary>
    StorageUsageRecord GetUsage(StoragePathConfig config);

    /// <summary>Returns the cached recursive folder size if still within FolderSizeScanIntervalMinutes, otherwise recomputes it.</summary>
    Task<long?> GetOrComputeFolderSizeAsync(StoragePathConfig config, CancellationToken ct = default);
}
