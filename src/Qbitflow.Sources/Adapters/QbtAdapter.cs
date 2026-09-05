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
            DownloadLimitBytesPerSec = t.DlLimit
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

    public Task SetCategoryAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, string category, CancellationToken ct = default) =>
        PostFormAsync(connection, "/api/v2/torrents/setCategory", new Dictionary<string, string>
        {
            ["hashes"] = string.Join('|', hashes),
            ["category"] = category
        }, ct);

    public Task SetLocationAsync(SourceConnectionInfo connection, IReadOnlyList<string> hashes, string location, CancellationToken ct = default) =>
        PostFormAsync(connection, "/api/v2/torrents/setLocation", new Dictionary<string, string>
        {
            ["hashes"] = string.Join('|', hashes),
            ["location"] = location
        }, ct);

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

    /// <summary>Returns the "SID=..." cookie header value to attach to later requests, or null if no credentials are configured (some setups whitelist local/LAN access).</summary>
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

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await ReadSnippetAsync(response, ct);
            _log.LogWarning(
                "qBittorrent auth returned HTTP {Status} {Reason} for {Instance} ({Url}). Body: {Body}",
                (int)response.StatusCode, response.StatusCode, connection.InstanceName, connection.BaseUrl, errBody);
            throw new InvalidOperationException(
                $"qBittorrent auth returned HTTP {(int)response.StatusCode} {response.StatusCode}."
                + (string.IsNullOrEmpty(errBody) ? string.Empty : $" {errBody}"));
        }

        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (!string.Equals(body, "Ok.", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning(
                "qBittorrent rejected the login for {Instance} ({Url}); response body: {Body}",
                connection.InstanceName, connection.BaseUrl, body);
            throw new InvalidOperationException(
                $"qBittorrent rejected the login (response: \"{body}\"). Verify the username/password; "
                + "qBittorrent also temporarily bans an IP after repeated failures -- check its Tools -> Log.");
        }

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var sidCookie = cookies.FirstOrDefault(c => c.StartsWith("SID=", StringComparison.OrdinalIgnoreCase));
            if (sidCookie is not null)
            {
                return sidCookie.Split(';')[0];
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
    }

    private sealed class QbtTorrentFileDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("progress")] public double Progress { get; set; }
    }
}
