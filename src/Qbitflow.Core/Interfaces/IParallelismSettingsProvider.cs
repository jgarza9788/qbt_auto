using Qbitflow.Core.Domain;

namespace Qbitflow.Core.Interfaces;

/// <summary>Reads the current global parallelism level (Settings page, AppSettings.ParallelismLevel).</summary>
public interface IParallelismSettingsProvider
{
    Task<ParallelismLevel> GetLevelAsync(CancellationToken ct = default);
}
