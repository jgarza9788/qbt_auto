using QbitFlow.Core.Abstractions;

namespace QbitFlow.Tests.Actions;

public sealed class FakeQbtActionTarget : IQbtActionTarget
{
    public Guid SourceId { get; } = Guid.NewGuid();
    public List<string> Calls { get; } = [];

    public Task AddTagAsync(string hash, string tag, CancellationToken ct) { Calls.Add($"addTag:{hash}:{tag}"); return Task.CompletedTask; }
    public Task RemoveTagAsync(string hash, string tag, CancellationToken ct) { Calls.Add($"removeTag:{hash}:{tag}"); return Task.CompletedTask; }
    public Task SetCategoryAsync(string hash, string category, bool enableAutoManagement, CancellationToken ct) { Calls.Add($"category:{hash}:{category}:{enableAutoManagement}"); return Task.CompletedTask; }
    public Task SetLocationAsync(string hash, string path, bool disableAutoManagement, CancellationToken ct) { Calls.Add($"move:{hash}:{path}"); return Task.CompletedTask; }
    public Task SetUploadLimitAsync(string hash, long bytesPerSecond, CancellationToken ct) { Calls.Add($"ul:{hash}:{bytesPerSecond}"); return Task.CompletedTask; }
    public Task SetDownloadLimitAsync(string hash, long bytesPerSecond, CancellationToken ct) { Calls.Add($"dl:{hash}:{bytesPerSecond}"); return Task.CompletedTask; }
    public Task PauseAsync(string hash, CancellationToken ct) { Calls.Add($"pause:{hash}"); return Task.CompletedTask; }
    public Task ResumeAsync(string hash, CancellationToken ct) { Calls.Add($"resume:{hash}"); return Task.CompletedTask; }
    public Task SetForceStartAsync(string hash, bool on, CancellationToken ct) { Calls.Add($"force:{hash}:{on}"); return Task.CompletedTask; }

    public Task<Stream> ExportTorrentAsync(string hash, CancellationToken ct)
    {
        Calls.Add($"export:{hash}");
        return Task.FromResult<Stream>(new MemoryStream("d8:announce0:e"u8.ToArray()));
    }
}
