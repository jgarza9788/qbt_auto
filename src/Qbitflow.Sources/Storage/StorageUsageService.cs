using System.Collections.Concurrent;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Sources.Storage;

public class StorageUsageService : IStorageUsageService
{
    private readonly ConcurrentDictionary<int, (long Size, DateTimeOffset ComputedAt)> _folderSizeCache = new();

    public StorageUsageRecord GetUsage(StoragePathConfig config)
    {
        try
        {
            if (!Directory.Exists(config.Path) && !File.Exists(config.Path))
            {
                return Unavailable(config, "Path does not exist or is not mounted.");
            }

            var drive = FindDriveForPath(config.Path);
            if (drive is null)
            {
                return Unavailable(config, "Could not resolve a mounted volume for this path.");
            }

            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            var used = total - free;
            _folderSizeCache.TryGetValue(config.Id, out var cached);

            return new StorageUsageRecord
            {
                StoragePathId = config.Id,
                Name = config.Name,
                Path = config.Path,
                Available = true,
                TotalBytes = total,
                UsedBytes = used,
                FreeBytes = free,
                UsedPercent = total > 0 ? Math.Round(used * 100.0 / total, 1) : 0,
                FreePercent = total > 0 ? Math.Round(free * 100.0 / total, 1) : 0,
                FolderSizeBytes = cached.ComputedAt == default ? null : cached.Size,
                FolderSizeComputedAt = cached.ComputedAt == default ? null : cached.ComputedAt
            };
        }
        catch (Exception ex)
        {
            return Unavailable(config, ex.Message);
        }
    }

    public async Task<long?> GetOrComputeFolderSizeAsync(StoragePathConfig config, CancellationToken ct = default)
    {
        if (_folderSizeCache.TryGetValue(config.Id, out var cached) &&
            DateTimeOffset.UtcNow - cached.ComputedAt < TimeSpan.FromMinutes(config.FolderSizeScanIntervalMinutes))
        {
            return cached.Size;
        }

        if (!Directory.Exists(config.Path))
        {
            return null;
        }

        var size = await Task.Run(() => ComputeDirectorySize(config.Path, ct), ct);
        _folderSizeCache[config.Id] = (size, DateTimeOffset.UtcNow);
        return size;
    }

    private static StorageUsageRecord Unavailable(StoragePathConfig config, string error) => new()
    {
        StoragePathId = config.Id,
        Name = config.Name,
        Path = config.Path,
        Available = false,
        Error = error
    };

    /// <summary>Iterative (not recursive) so a very deep tree can't blow the call stack; skips entries we can't read rather than failing the whole scan.</summary>
    private static long ComputeDirectorySize(string rootPath, CancellationToken ct)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                }
            }

            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                stack.Push(sub);
            }
        }

        return total;
    }

    private static DriveInfo? FindDriveForPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Where(d => fullPath.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
