using Qbitflow.Core.Domain.SourceData;

namespace Qbitflow.Core.Interfaces;

/// <summary>qBittorrent-specific capability: fetch one torrent's file list on demand (see TorrentFileRecord).</summary>
public interface IQbtTorrentFilesProvider
{
    Task<List<TorrentFileRecord>> GetFilesAsync(SourceConnectionInfo connection, string torrentHash, CancellationToken ct = default);
}
