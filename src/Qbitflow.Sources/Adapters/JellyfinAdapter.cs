using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Core.Interfaces;
using Qbitflow.Sources.Http;

namespace Qbitflow.Sources.Adapters;

public class JellyfinAdapter(IInstanceHttpClientFactory httpClientFactory) : ISourceAdapter
{
    public SourceType SourceType => SourceType.Jellyfin;

    public async Task<ConnectionTestResult> TestConnectionAsync(SourceConnectionInfo connection, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = httpClientFactory.CreateClient(connection);
            using var cts = HttpTimeouts.Create(ct, connection.TimeoutSeconds);
            using var request = BuildRequest(connection, "/System/Info");
            using var response = await client.SendAsync(request, cts.Token);
            await AdapterHttp.EnsureSuccessAsync(response, "Jellyfin", cts.Token);

            return new ConnectionTestResult { Success = true, Message = "Connected to Jellyfin.", Duration = sw.Elapsed };
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

        using var request = BuildRequest(connection, "/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Path,DateCreated");
        using var response = await client.SendAsync(request, cts.Token);
        await AdapterHttp.EnsureSuccessAsync(response, "Jellyfin", cts.Token);
        var payload = await response.Content.ReadFromJsonAsync<JellyfinItemsResponse>(cancellationToken: cts.Token);

        var items = (payload?.Items ?? []).Select(i => new MediaItemRecord
        {
            InstanceId = connection.InstanceId,
            InstanceName = connection.InstanceName,
            SourceType = SourceType.Jellyfin,
            ExternalKey = i.Id,
            Title = i.Name,
            MediaType = i.Type,
            FilePaths = string.IsNullOrEmpty(i.Path) ? [] : [i.Path],
            AddedAt = i.DateCreated
        }).ToList();

        return new SourceFetchResult { MediaItems = items };
    }

    /// <summary>
    /// Auth via the <c>Authorization: MediaBrowser Token="&lt;key&gt;"</c> scheme -- the original
    /// Emby/Jellyfin mechanism, accepted by every version from 10.x through 12.x. The legacy
    /// <c>?api_key=</c> query param and the <c>X-Emby-Token</c> header are both rejected
    /// (HTTP 401) by Jellyfin 12, so they are not used.
    /// </summary>
    private static HttpRequestMessage BuildRequest(SourceConnectionInfo connection, string pathAndQuery)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.BaseUrl.TrimEnd('/')}{pathAndQuery}");
        request.Headers.Add("Accept", "application/json");
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{connection.ApiKey}\"");
        }
        return request;
    }

    private sealed class JellyfinItemsResponse
    {
        [JsonPropertyName("Items")] public List<JellyfinItem> Items { get; set; } = [];
    }

    private sealed class JellyfinItem
    {
        [JsonPropertyName("Id")] public string Id { get; set; } = "";
        [JsonPropertyName("Name")] public string Name { get; set; } = "";
        [JsonPropertyName("Type")] public string Type { get; set; } = "";
        [JsonPropertyName("Path")] public string? Path { get; set; }
        [JsonPropertyName("DateCreated")] public DateTimeOffset? DateCreated { get; set; }
    }
}
