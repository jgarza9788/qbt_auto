using QBittorrent.Client;
using QbitFlow.Core.Contracts;

namespace QbitFlow.Sources.Qbt;

internal static class TorrentMapper
{
    public static TorrentView ToView(TorrentInfo t) => new()
    {
        Hash = t.Hash ?? "",
        Name = t.Name ?? "",
        Category = t.Category ?? "",
        Tags = t.Tags?.ToArray() ?? [],
        SavePath = t.SavePath ?? "",
        ContentPath = t.ContentPath ?? "",
        State = t.State.ToString(),
        Size = t.Size,
        Downloaded = t.Downloaded ?? 0,
        Uploaded = t.Uploaded ?? 0,
        Progress = t.Progress,
        Ratio = t.Ratio,
        DownloadLimit = t.DownloadLimit ?? 0,
        UploadLimit = t.UploadLimit ?? 0,
        NumSeeds = t.ConnectedSeeds,
        NumLeechs = t.ConnectedLeechers,
        ActiveTime = t.ActiveTime ?? TimeSpan.Zero,
        AddedOn = t.AddedOn,
        CompletionOn = t.CompletionOn,
        LastActivityTime = t.LastActivityTime,
        LastSeenComplete = t.LastSeenComplete,
        ForceStart = t.ForceStart,
        AutoManaged = t.AutomaticTorrentManagement,
    };
}
