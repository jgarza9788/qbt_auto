using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;

namespace QbitFlow.Sources.Jellyfin;

public sealed record JellyfinConnectionInfo(
    Guid SourceId,
    Uri BaseUrl,
    string ApiKey,
    /// <summary>"all" or a comma-separated list of user names.</summary>
    string UserScope);

/// <summary>
/// Jellyfin media source (REST/JSON, API-key auth). No native time-window history — flat
/// <c>UserData.PlayCount</c> is returned; the analytics job treats it as the "all" window.
/// </summary>
public sealed class JellyfinAdapter(HttpClient http, JellyfinConnectionInfo info) : IMediaSourceAdapter
{
    public SourceKind Kind => SourceKind.Jellyfin;
    public Guid SourceId => info.SourceId;

    public async Task<HealthResult> TestAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var res = await http.SendAsync(Get("/System/Info"), ct);
            res.EnsureSuccessStatusCode();
            return HealthResult.Healthy((int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return HealthResult.Unhealthy(ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<MediaRecord>> FetchMediaAsync(CancellationToken ct)
    {
        var url = "/Items?Recursive=true&IncludeItemTypes=Movie,Series,Episode" +
                  "&Fields=Path,Genres,ProductionYear,CommunityRating,RunTimeTicks,MediaSources";
        var page = await SendJsonAsync<ItemsResponse>(Get(url), ct);

        return (page?.Items ?? [])
            .Select(i => new MediaRecord(
                i.Id ?? i.Name ?? "",
                i.SeriesName ?? i.Name ?? "Unknown",
                MapType(i.Type),
                i.ProductionYear,
                i.CommunityRating,
                i.Genres ?? [],
                i.RunTimeTicks is { } t ? t / TimeSpan.TicksPerMillisecond : null,
                FilesOf(i)))
            .ToArray();
    }

    public async Task<IReadOnlyList<WatchRecord>> FetchWatchAsync(DateTimeOffset since, CancellationToken ct)
    {
        var userIds = await ResolveUsersAsync(ct);
        var grouped = new Dictionary<string, (string Type, int Count, DateTimeOffset? Last)>(StringComparer.OrdinalIgnoreCase);

        foreach (var userId in userIds)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"/Users/{userId}/Items?Recursive=true&IncludeItemTypes=Movie,Episode" +
                      "&Fields=Path&EnableUserData=true";
            var page = await SendJsonAsync<ItemsResponse>(Get(url), ct);

            foreach (var item in page?.Items ?? [])
            {
                var ud = item.UserData;
                if (ud is null || ud.PlayCount <= 0) continue;
                if (ud.LastPlayedDate is { } last && last < since) continue;

                var type = MapType(item.Type);
                var title = type == "episode" ? item.SeriesName ?? item.Name ?? "Unknown" : item.Name ?? "Unknown";
                var displayType = type == "episode" ? "show" : type;

                if (grouped.TryGetValue(title, out var agg))
                    grouped[title] = (agg.Type, agg.Count + ud.PlayCount, Max(agg.Last, ud.LastPlayedDate));
                else
                    grouped[title] = (displayType, ud.PlayCount, ud.LastPlayedDate);
            }
        }

        return grouped.Select(kv => new WatchRecord(kv.Key, kv.Key, kv.Value.Type, kv.Value.Count, kv.Value.Last))
            .ToArray();
    }

    // ---- helpers ----

    private async Task<IReadOnlyList<string>> ResolveUsersAsync(CancellationToken ct)
    {
        var users = await SendJsonAsync<List<JfUser>>(Get("/Users"), ct) ?? [];
        if (string.IsNullOrWhiteSpace(info.UserScope) || info.UserScope.Equals("all", StringComparison.OrdinalIgnoreCase))
            return users.Where(u => u.Id is not null).Select(u => u.Id!).ToArray();

        var wanted = info.UserScope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return users
            .Where(u => u.Id is not null && wanted.Contains(u.Name, StringComparer.OrdinalIgnoreCase))
            .Select(u => u.Id!)
            .ToArray();
    }

    private HttpRequestMessage Get(string pathAndQuery)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(info.BaseUrl, pathAndQuery.TrimStart('/')));
        req.Headers.Add("X-Emby-Token", info.ApiKey);
        return req;
    }

    private async Task<T?> SendJsonAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private static string MapType(string? jfType) => jfType switch
    {
        "Movie" => "movie",
        "Series" => "show",
        "Episode" => "episode",
        _ => "movie",
    };

    private static IReadOnlyList<MediaFile> FilesOf(JfItem i)
    {
        var files = new List<MediaFile>();
        if (!string.IsNullOrEmpty(i.Path))
            files.Add(new MediaFile(i.Path!, System.IO.Path.GetFileName(i.Path!), null));
        foreach (var ms in i.MediaSources ?? [])
            if (!string.IsNullOrEmpty(ms.Path))
                files.Add(new MediaFile(ms.Path!, System.IO.Path.GetFileName(ms.Path!), ms.Size));
        return files.DistinctBy(f => f.Path).ToArray();
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        (a, b) switch { (null, _) => b, (_, null) => a, _ => a > b ? a : b };

    // ---- DTOs ----

    private sealed class ItemsResponse { public List<JfItem>? Items { get; set; } }

    private sealed class JfItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? SeriesName { get; set; }
        public string? Type { get; set; }
        public int? ProductionYear { get; set; }
        public double? CommunityRating { get; set; }
        public List<string>? Genres { get; set; }
        public long? RunTimeTicks { get; set; }
        public string? Path { get; set; }
        public List<JfMediaSource>? MediaSources { get; set; }
        public JfUserData? UserData { get; set; }
    }

    private sealed class JfMediaSource { public string? Path { get; set; } public long? Size { get; set; } }

    private sealed class JfUserData
    {
        public int PlayCount { get; set; }
        public DateTimeOffset? LastPlayedDate { get; set; }
        public bool Played { get; set; }
    }

    private sealed class JfUser { public string? Id { get; set; } public string? Name { get; set; } }
}
