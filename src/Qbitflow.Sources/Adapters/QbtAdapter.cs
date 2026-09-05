using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Core.Interfaces;
using Qbitflow.Sources.Http;

namespace Qbitflow.Sources.Adapters;

public class QbtAdapter(IInstanceHttpClientFactory httpClientFactory, ILogger<QbtAdapter>? logger = null)
    : ISourceAdapter, IQbtTorrentFilesProvider, IQbtActionClient
{
    private readonly ILogger<QbtAdapter> _log = logger ?? NullLogger<QbtAdapter>.Instance;

    public SourceType SourceType => SourceType.Qbittorrent;

    public async Task<ConnectionTestResult> TestConnectionAsync(SourceConnectionInfo connection, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation("Testing qBittorrent connection to {Url} ({Instance})", connection.BaseUrl, connection.InstanceName);
        try
        {
            using var client = httpClientFactory.CreateClient(connection);
            using var cts = HttpTimeouts.Create(ct, connection.TimeoutSeconds);
            var sid = await LoginAsync(client, connection, cts.Token);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.BaseUrl.TrimEnd('/')}/api/v2/app/version");
            if (sid is not null)
            {
                request.Headers.Add("Cookie", sid);
            }

            using var response = await client.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();
            var version = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();

            _log.LogInformation("qBittorrent connection OK: {Instance} is {Version}", connection.InstanceName, version);
            return new ConnectionTestResult { Success = true, Message = $"Connected (qBittorrent {version}).", Duration = sw.Elapsed };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "qBittorrent connection test failed for {Instance} ({Url})", connection.InstanceName, connection.BaseUrl);
            var message = ex is HttpRequestException
                ? $"Could not reach qBittorrent at {connection.BaseUrl}: {ex.Message}"
                : ex.Message;
            return new ConnectionTestResult { Success = false, Message = message, Duration = sw.Elapsed };
        }
    }

    public async Task<SourceFetchResult> FetchAsync(SourceConnectionInfo connection, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient(connection);
        using var cts = HttpTimeouts.Create(ct, connection.TimeoutSeconds);
        var sid = await LoginAsync(client, connection, cts.Token);

        var torrents = await GetAsync<List<QbtTorrentDto>>(client, connection, "/api/v2/torrents/info", sid, cts.Token) ?? [];

        var records = torrents.Select(t => new TorrentRecord
        {
            InstanceId = connection.InstanceId,
            InstanceName = connection.InstanceName,
            Hash = t.Hash,
            Name = t.Name,
            Category = string.IsNullOrEmpty(t.Category) ? null : t.Category,
            Tags = string.IsNullOrWhiteSpace(t.Tags)
                ? []
                : t.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
            SavePath = t.SavePath,
            ContentPath = t.ContentPath,
            SizeBytes = t.Size,
            Progress = t.Progress,
            State = t.State,
            DownloadedBytes = t.Downloaded,
            UploadedBytes = t.Uploaded,
            Ratio = t.Ratio,
            AddedOn = t.AddedOn > 0 ? DateTimeOffset.FromUnixTimeSeconds(t.AddedOn) : null,
            CompletionOn = t.CompletionOn > 0 ? DateTimeOffset.FromUnixTimeSeconds(t.CompletionOn) : null,
            UploadLimitBytesPerSec = t.UpLimit,
            DownloadLimitBytesPerSec = t.DlLimit,
            Tracker = string.IsNullOrEmpty(t.Tracker) ? null : t.Tracker,
            TotalSizeBytes = t.TotalSize,
            AmountLeftBytes = t.AmountLeft,
            CompletedBytes = t.Completed,
            DownloadSpeedBytesPerSec = t.DlSpeed,
            UploadSpeedBytesPerSec = t.UpSpeed,
            EtaSeconds = t.Eta,
            SeedingTimeSeconds = t.SeedingTime,
            ActiveTimeSeconds = t.TimeActive,
            ConnectedSeeds = t.NumSeeds,
            TotalSeeds = t.NumComplete,
            ConnectedLeechers = t.NumLeechs,
            TotalLeechers = t.NumIncomplete,
            Availability = t.Availability,
            AutoTmmEnabled = t.AutoTmm,
            RatioLimit = t.RatioLimit,
            SeedingTimeLimitMinutes = t.SeedingTimeLimit,
            LastActivityOn = t.LastActivity > 0 ? DateTimeOffset.FromUnixTimeSeconds(t.LastActivity) : null,
            SeenCompleteOn = t.SeenComplete > 0 ? DateTimeOffset.FromUnixTimeSeconds(t.SeenComplete) : null
        }).ToList();

        return new SourceFetchResult { Torrents = records };
    }

    public async Task<List<TorrentFileRecord>> GetFilesAsync(SourceConnectionInfo connection, string torrentHash, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient(connection);
        using var cts = HttpTimeouts.Create(ct, connection.TimeoutSeconds);
        var sid = await LoginAsync(client, connection, cts.Token);

        var files = await GetAsync<List<QbtTorrentFileDto>>(
            client, connection, $"/api/v2/torrents/files?hash={Uri.EscapeDataString(torrentHash)}", sid, cts.Token) ?? [];

        return files.Select(f => new TorrentFileRecord
        {
            TorrentHash = torrentHash,
            FilePath = f.Name,
            SizeBytes = f.Size,
            Progress = f.Progress
        }).ToList();
    }

    public async Task<Dictionary<string, QbtTorrentState>> GetCurrentStateAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient(connection);
        using var cts = HttpTimeouts.Create(ct, connection.TimeoutSeconds);
        var sid = await LoginAsync(client, connection, cts.Token);

        var hashParam = Uri.EscapeDataString(string.Join('|', hashes));
        var torrents = await GetAsync<List<QbtTorrentDto>>(client, connection, $"/api/v2/torrents/info?hashes={hashParam}", sid, cts.Token) ?? [];

        return torrents.ToDictionary(t => t.Hash, t => new QbtTorrentState
        {
            Hash = t.Hash,
            Tags = string.IsNullOrWhiteSpace(t.Tags)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : t.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase),
            Category = string.IsNullOrEmpty(t.Category) ? null : t.Category,
            SavePath = t.SavePath,
            UploadLimitBytesPerSec = t.UpLimit,
            DownloadLimitBytesPerSec = t.DlLimit
        });
    }

    public Task AddTagsAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, IReadOnlyList<string> tags, CancellationToken ct = default) =>
        PostFormAsync(connection, "/api/v2/torrents/addTags", new Dictionary<string, string>
        {
            ["hashes"] = string.Join('|', hashes),
            ["tags"] = string.Join(',', tags)
        }, ct);

    public Task RemoveTagsAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, IReadOnlyList<string> tags, CancellationToken ct = default) =>
        PostFormAsync(connection, "/api/v2/torrents/removeTags", new Dictionary<string, string>
        {
            ["hashes"] = string.Join('|', hashes),
            ["tags"] = string.Join(',', tags)
        }, ct);

    public async Task SetCategoryAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, string category, CancellationToken ct = default)
    {
        var joinedHashes = string.Join('|', hashes);

        await PostFormAsync(connection, "/api/v2/torrents/setCategory", new Dictionary<string, string>
        {
            ["hashes"] = joinedHashes,
            ["category"] = category
        }, ct);

        // Re-enable Automatic Torrent Management so qBittorrent relocates the torrents to the
        // category's configured save path (the mirror of SetLocationAsync, which turns it off).
        await PostFormAsync(connection, "/api/v2/torrents/setAutoManagement", new Dictionary<string, string>
        {
            ["hashes"] = joinedHashes,
            ["enable"] = "true"
        }, ct);
    }

    public async Task SetLocationAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, string location, CancellationToken ct = default)
    {
        var joinedHashes = string.Join('|', hashes);

        // qBittorrent ignores (or immediately re-moves) a manual location while Automatic Torrent
        // Management is on, so switch it off for these torrents before pointing them at the new path.
        await PostFormAsync(connection, "/api/v2/torrents/setAutoManagement", new Dictionary<string, string>
        {
            ["hashes"] = joinedHashes,
            ["enable"] = "false"
        }, ct);

        await PostFormAsync(connection, "/api/v2/torrents/setLocation", new Dictionary<string, string>
        {
            ["hashes"] = joinedHashes,
            ["location"] = location
        }, ct);
    }

    public Task SetUploadLimitAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, long bytesPerSec, CancellationToken ct = default) =>
        PostFormAsync(connection, "/api/v2/torrents/setUploadLimit", new Dictionary<string, string>
        {
            ["hashes"] = string.Join('|', hashes),
            ["limit"] = bytesPerSec.ToString()
        }, ct);

    public Task SetDownloadLimitAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, long bytesPerSec, CancellationToken ct = default) =>
        PostFormAsync(connection, "/api/v2/torrents/setDownloadLimit", new Dictionary<string, string>
        {
            ["hashes"] = string.Join('|', hashes),
            ["limit"] = bytesPerSec.ToString()
        }, ct);

    private async Task PostFormAsync(SourceConnectionInfo connection, string path, Dictionary<string, string> form, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(connection);
        using var cts = HttpTimeouts.Create(ct, connection.TimeoutSeconds);
        var sid = await LoginAsync(client, connection, cts.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{connection.BaseUrl.TrimEnd('/')}{path}")
        {
            Content = new FormUrlEncodedContent(form)
        };
        if (sid is not null)
        {
            request.Headers.Add("Cookie", sid);
        }

        using var response = await client.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Logs in and returns the session cookie ("&lt;name&gt;=&lt;value&gt;") to attach to later requests,
    /// or null when no credentials are configured (some setups whitelist local/LAN access).
    /// Handles both the classic contract (HTTP 200, body "Ok.", cookie "SID=") and qBittorrent
    /// 5.2+ (HTTP 204, empty body, cookie "QBT_SID_&lt;port&gt;="). A wrong password is HTTP 401
    /// (5.2+) or HTTP 200 with body "Fails." (classic).
    /// </summary>
    private async Task<string?> LoginAsync(HttpClient client, SourceConnectionInfo connection, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(connection.Username))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{connection.BaseUrl.TrimEnd('/')}/api/v2/auth/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = connection.Username,
                ["password"] = connection.Password ?? string.Empty
            })
        };

        using var response = await client.SendAsync(request, ct);
        var body = (await ReadSnippetAsync(response, ct));

        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "qBittorrent auth returned HTTP {Status} {Reason} for {Instance} ({Url}). Body: {Body}",
                (int)response.StatusCode, response.StatusCode, connection.InstanceName, connection.BaseUrl, body);
            var hint = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? " Check the username/password; qBittorrent also temporarily bans an IP after repeated failures -- see its Tools -> Log."
                : string.Empty;
            // Skip the body snippet when it's just the status text repeated (e.g. "Unauthorized").
            var detail = string.IsNullOrEmpty(body) || string.Equals(body, response.StatusCode.ToString(), StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $" {body}";
            throw new InvalidOperationException(
                $"qBittorrent auth returned HTTP {(int)response.StatusCode} {response.StatusCode}." + detail + hint);
        }

        // Some builds answer a bad login with HTTP 200 + "Fails." rather than a 4xx.
        if (string.Equals(body, "Fails.", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning(
                "qBittorrent rejected the login for {Instance} ({Url}); response body: {Body}",
                connection.InstanceName, connection.BaseUrl, body);
            throw new InvalidOperationException(
                "qBittorrent rejected the login (response: \"Fails.\"). Verify the username/password; "
                + "qBittorrent also temporarily bans an IP after repeated failures -- check its Tools -> Log.");
        }

        // Success: HTTP 2xx with an empty body (5.2+) or "Ok." (classic). Grab the session
        // cookie -- its name is "SID" on older builds, "QBT_SID_<port>" on 5.2+.
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                var pair = cookie.Split(';', 2)[0].Trim();
                var name = pair.Split('=', 2)[0];
                if (name.Contains("SID", StringComparison.OrdinalIgnoreCase))
                {
                    return pair;
                }
            }
        }

        throw new InvalidOperationException("qBittorrent login succeeded but no session cookie was returned.");
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
            return text.Length <= 512 ? text : text[..512];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<T?> GetAsync<T>(HttpClient client, SourceConnectionInfo connection, string path, string? sidCookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.BaseUrl.TrimEnd('/')}{path}");
        if (sidCookie is not null)
        {
            request.Headers.Add("Cookie", sidCookie);
        }

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private sealed class QbtTorrentDto
    {
        [JsonPropertyName("hash")] public string Hash { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("tags")] public string? Tags { get; set; }
        [JsonPropertyName("save_path")] public string? SavePath { get; set; }
        [JsonPropertyName("content_path")] public string? ContentPath { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("progress")] public double Progress { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("downloaded")] public long Downloaded { get; set; }
        [JsonPropertyName("uploaded")] public long Uploaded { get; set; }
        [JsonPropertyName("ratio")] public double Ratio { get; set; }
        [JsonPropertyName("added_on")] public long AddedOn { get; set; }
        [JsonPropertyName("completion_on")] public long CompletionOn { get; set; }
        [JsonPropertyName("up_limit")] public long UpLimit { get; set; }
        [JsonPropertyName("dl_limit")] public long DlLimit { get; set; }
        [JsonPropertyName("tracker")] public string? Tracker { get; set; }
        [JsonPropertyName("total_size")] public long TotalSize { get; set; }
        [JsonPropertyName("amount_left")] public long AmountLeft { get; set; }
        [JsonPropertyName("completed")] public long Completed { get; set; }
        [JsonPropertyName("dlspeed")] public long DlSpeed { get; set; }
        [JsonPropertyName("upspeed")] public long UpSpeed { get; set; }
        [JsonPropertyName("eta")] public long Eta { get; set; }
        [JsonPropertyName("seeding_time")] public long SeedingTime { get; set; }
        [JsonPropertyName("time_active")] public long TimeActive { get; set; }
        [JsonPropertyName("num_seeds")] public long NumSeeds { get; set; }
        [JsonPropertyName("num_complete")] public long NumComplete { get; set; }
        [JsonPropertyName("num_leechs")] public long NumLeechs { get; set; }
        [JsonPropertyName("num_incomplete")] public long NumIncomplete { get; set; }
        [JsonPropertyName("availability")] public double Availability { get; set; }
        [JsonPropertyName("auto_tmm")] public bool AutoTmm { get; set; }
        [JsonPropertyName("ratio_limit")] public double RatioLimit { get; set; }
        [JsonPropertyName("seeding_time_limit")] public long SeedingTimeLimit { get; set; }
        [JsonPropertyName("last_activity")] public long LastActivity { get; set; }
        [JsonPropertyName("seen_complete")] public long SeenComplete { get; set; }
    }

    private sealed class QbtTorrentFileDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("progress")] public double Progress { get; set; }
    }
}
