using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Sources.Http;

namespace Qbitflow.Sources.Adapters;

/// <summary>
/// Streamystats, like Jellystat and Jellyglance, has no single stable public "history"
/// API contract -- these defaults are a starting point for a typical self-hosted
/// instance, not a guarantee. Set historyPath/resultsPath/fieldMap in this instance's
/// ExtraConfigJson to match your actual deployment (its playback-session endpoint is
/// server-scoped, e.g. "/api/servers/1/statistics/history").
/// </summary>
public class StreamystatsAdapter(IInstanceHttpClientFactory httpClientFactory) : RestHistoryAdapterBase(httpClientFactory)
{
    public override SourceType SourceType => SourceType.Streamystats;

    protected override string DefaultHistoryPath => "/api/history";
    protected override string DefaultResultsPath => "";

    protected override IReadOnlyDictionary<string, string> DefaultFieldMap => new Dictionary<string, string>
    {
        ["title"] = "item_name",
        ["filePath"] = "file_path",
        ["user"] = "user_name",
        ["watchedAt"] = "start_time",
        ["percent"] = "percent_complete"
    };

    protected override void ApplyAuth(HttpRequestMessage request, SourceConnectionInfo connection)
    {
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            request.Headers.Add("X-Api-Key", connection.ApiKey);
        }
    }
}
