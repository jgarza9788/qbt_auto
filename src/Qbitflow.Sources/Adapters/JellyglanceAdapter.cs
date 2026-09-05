using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Sources.Http;

namespace Qbitflow.Sources.Adapters;

/// <summary>
/// Jellyglance has no single stable public "history" API contract either -- these
/// defaults are a starting point only. Set historyPath/resultsPath/fieldMap in this
/// instance's ExtraConfigJson to match your actual deployment.
/// </summary>
public class JellyglanceAdapter(IInstanceHttpClientFactory httpClientFactory) : RestHistoryAdapterBase(httpClientFactory)
{
    public override SourceType SourceType => SourceType.Jellyglance;

    protected override string DefaultHistoryPath => "/api/history";
    protected override string DefaultResultsPath => "";

    protected override IReadOnlyDictionary<string, string> DefaultFieldMap => new Dictionary<string, string>
    {
        ["title"] = "title",
        ["filePath"] = "path",
        ["user"] = "user",
        ["watchedAt"] = "watchedAt",
        ["percent"] = "percent"
    };

    protected override void ApplyAuth(HttpRequestMessage request, SourceConnectionInfo connection)
    {
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            request.Headers.Add("X-Api-Key", connection.ApiKey);
        }
    }
}
