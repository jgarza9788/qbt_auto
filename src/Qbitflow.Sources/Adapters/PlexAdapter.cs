using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Core.Interfaces;
using Qbitflow.Sources.Http;

namespace Qbitflow.Sources.Adapters;

public class PlexAdapter(IInstanceHttpClientFactory httpClientFactory) : ISourceAdapter
{
    public SourceType SourceType => SourceType.Plex;

    public async Task<ConnectionTestResult> TestConnectionAsync(SourceConnectionInfo connection, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = httpClientFactory.CreateClient(connection);
            using var cts = HttpTimeouts.Create(ct, connection.TimeoutSeconds);
            using var request = BuildRequest(connection, "/identity");
            using var response = await client.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();

            return new ConnectionTestResult { Success = true, Message = "Connected to Plex Media Server.", Duration = sw.Elapsed };
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult { Success = false, Message = ex.Message, Duration = sw.Elapsed };
        }
    }

    public async Task<SourceFetchResult> FetchAsync(SourceConnectionInfo connection, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient(connection);
        using var cts = HttpTimeouts.Create(ct, connection.TimeoutSeconds);

        var sections = await GetJsonAsync<PlexSectionsResponse>(client, connection, "/library/sections", cts.Token);
        var directories = sections?.MediaContainer?.Directory ?? [];

        var items = new List<MediaItemRecord>();
        foreach (var dir in directories)
        {
            // "show" sections list shows by default; ?type=4 flattens to episodes, which is
            // what we actually need for file-path matching against torrent content.
            var typeFilter = dir.Type == "show" ? "?type=4" : string.Empty;
            var path = $"/library/sections/{dir.Key}/all{typeFilter}";

            var page = await GetJsonAsync<PlexItemsResponse>(client, connection, path, cts.Token);
            foreach (var meta in page?.MediaContainer?.Metadata ?? [])
            {
                var filePaths = (meta.Media ?? [])
                    .SelectMany(m => m.Part ?? [])
                    .Select(p => p.File)
                    .Where(f => !string.IsNullOrEmpty(f))
                    .Select(f => f!)
                    .ToList();

                items.Add(new MediaItemRecord
                {
                    InstanceId = connection.InstanceId,
                    InstanceName = connection.InstanceName,
                    SourceType = SourceType.Plex,
                    ExternalKey = meta.RatingKey,
                    Title = meta.Title,
                    MediaType = meta.Type,
                    FilePaths = filePaths,
                    AddedAt = meta.AddedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(meta.AddedAt) : null
                });
            }
        }

        return new SourceFetchResult { MediaItems = items };
    }

    private static HttpRequestMessage BuildRequest(SourceConnectionInfo connection, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.BaseUrl.TrimEnd('/')}{path}");
        request.Headers.Add("Accept", "application/json");
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            request.Headers.Add("X-Plex-Token", connection.ApiKey);
        }
        return request;
    }

    private static async Task<T?> GetJsonAsync<T>(HttpClient client, SourceConnectionInfo connection, string path, CancellationToken ct)
    {
        using var request = BuildRequest(connection, path);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private sealed class PlexSectionsResponse
    {
        [JsonPropertyName("MediaContainer")] public PlexSectionsContainer? MediaContainer { get; set; }
    }

    private sealed class PlexSectionsContainer
    {
        [JsonPropertyName("Directory")] public List<PlexDirectory> Directory { get; set; } = [];
    }

    private sealed class PlexDirectory
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
    }

    private sealed class PlexItemsResponse
    {
        [JsonPropertyName("MediaContainer")] public PlexItemsContainer? MediaContainer { get; set; }
    }

    private sealed class PlexItemsContainer
    {
        [JsonPropertyName("Metadata")] public List<PlexMetadata> Metadata { get; set; } = [];
    }

    private sealed class PlexMetadata
    {
        [JsonPropertyName("ratingKey")] public string RatingKey { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("addedAt")] public long AddedAt { get; set; }
        [JsonPropertyName("Media")] public List<PlexMedia>? Media { get; set; }
    }

    private sealed class PlexMedia
    {
        [JsonPropertyName("Part")] public List<PlexPart>? Part { get; set; }
    }

    private sealed class PlexPart
    {
        [JsonPropertyName("file")] public string? File { get; set; }
    }
}
