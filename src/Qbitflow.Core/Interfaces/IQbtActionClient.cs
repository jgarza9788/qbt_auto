using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Core.Interfaces;

/// <summary>qBittorrent write operations, batched by hash where the API allows it (one call per instance per action, not per torrent).</summary>
public interface IQbtActionClient
{
    Task<Dictionary<string, QbtTorrentState>> GetCurrentStateAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, CancellationToken ct = default);

    Task AddTagsAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, IReadOnlyList<string> tags, CancellationToken ct = default);

    Task RemoveTagsAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, IReadOnlyList<string> tags, CancellationToken ct = default);

    Task SetCategoryAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, string category, CancellationToken ct = default);

    Task SetLocationAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, string location, CancellationToken ct = default);

    Task SetUploadLimitAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, long bytesPerSec, CancellationToken ct = default);

    Task SetDownloadLimitAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, long bytesPerSec, CancellationToken ct = default);
}
