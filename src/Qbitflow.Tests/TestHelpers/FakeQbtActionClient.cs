using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Core.Interfaces;

namespace Qbitflow.Tests.TestHelpers;

/// <summary>
/// In-memory stand-in for qBittorrent: write calls mutate State immediately (matching
/// real qBt's near-instant application of tag/category/limit changes), except
/// SetLocationAsync, which only converges after ConvergeAfterPolls subsequent
/// GetCurrentStateAsync calls -- modeling that a physical file move genuinely takes time.
/// </summary>
internal class FakeQbtActionClient : IQbtActionClient
{
    public Dictionary<string, QbtTorrentState> State { get; } = new();
    public List<string> Calls { get; } = [];
    public int ConvergeAfterPolls { get; set; }

    private readonly Dictionary<string, string> _pendingMoves = new();
    private readonly Dictionary<string, int> _pollCounts = new();

    public Task<Dictionary<string, QbtTorrentState>> GetCurrentStateAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, CancellationToken ct = default)
    {
        var result = new Dictionary<string, QbtTorrentState>();
        foreach (var h in hashes)
        {
            if (!State.TryGetValue(h, out var s))
            {
                continue;
            }

            if (_pendingMoves.TryGetValue(h, out var dest))
            {
                _pollCounts[h] = _pollCounts.GetValueOrDefault(h) + 1;
                if (_pollCounts[h] > ConvergeAfterPolls)
                {
                    s = new QbtTorrentState
                    {
                        Hash = s.Hash,
                        Tags = s.Tags,
                        Category = s.Category,
                        SavePath = dest,
                        UploadLimitBytesPerSec = s.UploadLimitBytesPerSec,
                        DownloadLimitBytesPerSec = s.DownloadLimitBytesPerSec
                    };
                    State[h] = s;
                    _pendingMoves.Remove(h);
                }
            }

            result[h] = s;
        }

        return Task.FromResult(result);
    }

    public Task AddTagsAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        Calls.Add($"AddTags:{string.Join(',', hashes)}:{string.Join(',', tags)}");
        foreach (var h in hashes)
        foreach (var t in tags)
        {
            State[h].Tags.Add(t);
        }
        return Task.CompletedTask;
    }

    public Task RemoveTagsAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        Calls.Add($"RemoveTags:{string.Join(',', hashes)}:{string.Join(',', tags)}");
        foreach (var h in hashes)
        foreach (var t in tags)
        {
            State[h].Tags.Remove(t);
        }
        return Task.CompletedTask;
    }

    public Task SetCategoryAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, string category, CancellationToken ct = default)
    {
        Calls.Add($"SetCategory:{string.Join(',', hashes)}:{category}");
        foreach (var h in hashes)
        {
            var s = State[h];
            State[h] = new QbtTorrentState { Hash = s.Hash, Tags = s.Tags, Category = category, SavePath = s.SavePath, UploadLimitBytesPerSec = s.UploadLimitBytesPerSec, DownloadLimitBytesPerSec = s.DownloadLimitBytesPerSec };
        }
        return Task.CompletedTask;
    }

    public Task SetLocationAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, string location, CancellationToken ct = default)
    {
        Calls.Add($"SetLocation:{string.Join(',', hashes)}:{location}");
        foreach (var h in hashes)
        {
            _pendingMoves[h] = location;
            _pollCounts[h] = 0;
        }
        return Task.CompletedTask;
    }

    public Task SetUploadLimitAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, long bytesPerSec, CancellationToken ct = default)
    {
        Calls.Add($"SetUploadLimit:{string.Join(',', hashes)}:{bytesPerSec}");
        foreach (var h in hashes)
        {
            var s = State[h];
            State[h] = new QbtTorrentState { Hash = s.Hash, Tags = s.Tags, Category = s.Category, SavePath = s.SavePath, UploadLimitBytesPerSec = bytesPerSec, DownloadLimitBytesPerSec = s.DownloadLimitBytesPerSec };
        }
        return Task.CompletedTask;
    }

    public Task SetDownloadLimitAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, long bytesPerSec, CancellationToken ct = default)
    {
        Calls.Add($"SetDownloadLimit:{string.Join(',', hashes)}:{bytesPerSec}");
        foreach (var h in hashes)
        {
            var s = State[h];
            State[h] = new QbtTorrentState { Hash = s.Hash, Tags = s.Tags, Category = s.Category, SavePath = s.SavePath, UploadLimitBytesPerSec = s.UploadLimitBytesPerSec, DownloadLimitBytesPerSec = bytesPerSec };
        }
        return Task.CompletedTask;
    }
}
