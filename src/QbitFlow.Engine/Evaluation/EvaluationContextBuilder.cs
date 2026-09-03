using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Contracts;

namespace QbitFlow.Engine.Evaluation;

/// <summary>
/// Builds the single <c>&lt;placeholder&gt;</c> dictionary a rule is evaluated against: drive fields
/// (per run), torrent fields (per torrent), and media / derived fields (from the enricher).
/// Replaces the copy-pasted <c>globalDict + T + plexdata</c> concat in the legacy handlers.
/// Merge order (last wins): drive ← torrent ← media/derived.
/// </summary>
public sealed class EvaluationContextBuilder(IMediaEnricher mediaEnricher)
{
    public async Task<IReadOnlyDictionary<string, object?>> BuildAsync(
        Guid qbtInstanceId,
        TorrentView torrent,
        IReadOnlyDictionary<string, object?> driveSnapshot,
        CancellationToken ct)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (k, v) in driveSnapshot)
            dict[k] = v;

        foreach (var (k, v) in TorrentFields(torrent))
            dict[k] = v;

        var media = await mediaEnricher.EnrichAsync(qbtInstanceId, torrent, ct);
        foreach (var (k, v) in media)
            dict[k] = v;

        return dict;
    }

    public static IEnumerable<KeyValuePair<string, object?>> TorrentFields(TorrentView t)
    {
        yield return new("Hash", t.Hash);
        yield return new("Name", t.Name);
        yield return new("Category", t.Category);
        yield return new("Tags", t.TagsCsv);
        yield return new("SavePath", t.SavePath);
        yield return new("ContentPath", t.ContentPath);
        yield return new("State", t.State);
        yield return new("Size", t.Size);
        yield return new("Downloaded", t.Downloaded);
        yield return new("Uploaded", t.Uploaded);
        yield return new("Progress", t.Progress);
        yield return new("Ratio", t.Ratio);
        yield return new("NumSeeds", t.NumSeeds);
        yield return new("NumLeechs", t.NumLeechs);
        yield return new("DownloadLimit", t.DownloadLimit);
        yield return new("UploadLimit", t.UploadLimit);
        yield return new("ActiveTime", t.ActiveTime.Ticks);          // legacy parity: ticks
        yield return new("ActiveTimeSeconds", t.ActiveTimeSeconds);
        yield return new("ActiveTimeDays", t.ActiveTime.TotalDays);
        yield return new("AddedOn", Iso(t.AddedOn));
        yield return new("CompletionOn", Iso(t.CompletionOn));
        yield return new("LastActivityTime", Iso(t.LastActivityTime));
        yield return new("LastSeenComplete", Iso(t.LastSeenComplete));
        yield return new("ForceStart", t.ForceStart);
        yield return new("AutoManaged", t.AutoManaged);
    }

    private static string Iso(DateTimeOffset? d) =>
        d?.ToUniversalTime().ToString("o") ?? DateTimeOffset.UnixEpoch.ToString("o");
}
