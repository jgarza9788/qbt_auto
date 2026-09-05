using System.Diagnostics;
using System.Text.Json;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Core.Interfaces;
using Qbitflow.Sources.Http;
using Qbitflow.Sources.Json;

namespace Qbitflow.Sources.Adapters;

/// <summary>
/// Shared "fetch a JSON array of watch/play events from a REST endpoint" logic for
/// Tautulli, Jellystat, and Jellyglance -- they differ only in URL shape, auth
/// placement, and JSON field names, all of which are supplied by the subclass (and
/// further overridable per-instance via ExtraConfigJson, see RestHistoryConfig).
/// </summary>
public abstract class RestHistoryAdapterBase(IInstanceHttpClientFactory httpClientFactory) : ISourceAdapter
{
    public abstract SourceType SourceType { get; }
    protected abstract string DefaultHistoryPath { get; }
    protected abstract string DefaultResultsPath { get; }
    protected abstract IReadOnlyDictionary<string, string> DefaultFieldMap { get; }

    /// <summary>Substitutes {apiKey} into the configured history path for sources that take the key as a query parameter.</summary>
    protected virtual string BuildUrl(SourceConnectionInfo connection, string historyPath) =>
        $"{connection.BaseUrl.TrimEnd('/')}{historyPath.Replace("{apiKey}", Uri.EscapeDataString(connection.ApiKey ?? string.Empty))}";

    /// <summary>Override to attach a header-based API key instead of (or in addition to) a query parameter.</summary>
    protected virtual void ApplyAuth(HttpRequestMessage request, SourceConnectionInfo connection)
    {
    }

    public async Task<SourceFetchResult> FetchAsync(SourceConnectionInfo connection, CancellationToken ct = default)
    {
        var config = RestHistoryConfig.Parse(connection.ExtraConfigJson, DefaultHistoryPath, DefaultResultsPath, DefaultFieldMap);
        var records = await FetchHistoryAsync(connection, config, ct);
        return new SourceFetchResult { WatchHistory = records };
    }

    public virtual async Task<ConnectionTestResult> TestConnectionAsync(SourceConnectionInfo connection, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var config = RestHistoryConfig.Parse(connection.ExtraConfigJson, DefaultHistoryPath, DefaultResultsPath, DefaultFieldMap);
            var records = await FetchHistoryAsync(connection, config, ct);
            return new ConnectionTestResult { Success = true, Message = $"Connected ({records.Count} history record(s) found).", Duration = sw.Elapsed };
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult { Success = false, Message = ex.Message, Duration = sw.Elapsed };
        }
    }

    private async Task<List<WatchHistoryRecord>> FetchHistoryAsync(SourceConnectionInfo connection, RestHistoryConfig config, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(connection);
        using var cts = HttpTimeouts.Create(ct, connection.TimeoutSeconds);
        var url = BuildUrl(connection, config.HistoryPath);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request, connection);

        using var response = await client.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

        var array = JsonPathResolver.Resolve(doc.RootElement, config.ResultsPath);
        var records = new List<WatchHistoryRecord>();
        if (array is { ValueKind: JsonValueKind.Array } arr)
        {
            foreach (var item in arr.EnumerateArray())
            {
                records.Add(new WatchHistoryRecord
                {
                    InstanceId = connection.InstanceId,
                    InstanceName = connection.InstanceName,
                    MediaTitle = JsonPathResolver.GetString(item, config.FieldMap.GetValueOrDefault("title")),
                    FilePath = JsonPathResolver.GetString(item, config.FieldMap.GetValueOrDefault("filePath")),
                    UserName = JsonPathResolver.GetString(item, config.FieldMap.GetValueOrDefault("user")),
                    WatchedAt = JsonPathResolver.GetUnixSeconds(item, config.FieldMap.GetValueOrDefault("watchedAt")),
                    PercentComplete = JsonPathResolver.GetDouble(item, config.FieldMap.GetValueOrDefault("percent"))
                });
            }
        }

        return records;
    }
}
