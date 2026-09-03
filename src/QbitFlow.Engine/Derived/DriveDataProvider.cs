namespace QbitFlow.Engine.Derived;

/// <summary>
/// Snapshots per-drive space as <c>&lt;mount&gt;_TotalSizeGB</c> / <c>_FreeSizeGB</c> /
/// <c>_UsedSizeGB</c> / <c>_PercentUsed</c> fields. Key names are kept verbatim from the legacy
/// <c>Utils.Drives.getDriveData</c> so existing criteria keep working.
/// </summary>
public sealed class DriveDataProvider
{
    private const double Gib = 1024d * 1024d * 1024d;

    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;

                var totalGb = drive.TotalSize / Gib;
                var freeGb = drive.TotalFreeSpace / Gib;
                var usedGb = (drive.TotalSize - drive.TotalFreeSpace) / Gib;

                var name = drive.Name;
                data[$"{name}_TotalSizeGB"] = totalGb;
                data[$"{name}_FreeSizeGB"] = freeGb;
                data[$"{name}_UsedSizeGB"] = usedGb;
                data[$"{name}_PercentUsed"] = totalGb > 0 ? usedGb / totalGb : 0d;
            }
            catch
            {
                // A drive we can't stat is simply omitted (parity with the legacy behaviour).
            }
        }

        return data;
    }
}
