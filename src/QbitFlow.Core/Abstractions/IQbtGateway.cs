using QbitFlow.Core.Contracts;

namespace QbitFlow.Core.Abstractions;

public sealed record HealthResult(bool Ok, int LatencyMs, string? Error)
{
    public static HealthResult Healthy(int latencyMs) => new(true, latencyMs, null);
    public static HealthResult Unhealthy(string error, int latencyMs = 0) => new(false, latencyMs, error);
}

/// <summary>Reads torrent state from one qBittorrent instance.</summary>
public interface IQbtAdapter
{
    Guid SourceId { get; }
    Task<HealthResult> TestAsync(CancellationToken ct);
    Task<IReadOnlyList<TorrentView>> FetchTorrentsAsync(CancellationToken ct);
}

/// <summary>Mutates torrents on one qBittorrent instance. One method per capability.</summary>
public interface IQbtActionTarget
{
    Guid SourceId { get; }

    Task AddTagAsync(string hash, string tag, CancellationToken ct);
    Task RemoveTagAsync(string hash, string tag, CancellationToken ct);
    Task SetCategoryAsync(string hash, string category, bool enableAutoManagement, CancellationToken ct);
    Task SetLocationAsync(string hash, string path, bool disableAutoManagement, CancellationToken ct);
    Task SetUploadLimitAsync(string hash, long bytesPerSecond, CancellationToken ct);
    Task SetDownloadLimitAsync(string hash, long bytesPerSecond, CancellationToken ct);
    Task PauseAsync(string hash, CancellationToken ct);
    Task ResumeAsync(string hash, CancellationToken ct);
    Task SetForceStartAsync(string hash, bool on, CancellationToken ct);

    /// <summary>Downloads the <c>.torrent</c> file for a hash (raw WebUI API — the client lib has no wrapper).</summary>
    Task<Stream> ExportTorrentAsync(string hash, CancellationToken ct);
}

/// <summary>Resolves per-instance qBittorrent gateways from a <c>SourceConnection</c> id.</summary>
public interface IQbtGatewayFactory
{
    IQbtAdapter GetAdapter(Guid sourceConnectionId);
    IQbtActionTarget GetActionTarget(Guid sourceConnectionId);
}
