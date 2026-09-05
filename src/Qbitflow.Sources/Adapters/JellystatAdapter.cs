using Qbitflow.Core.Domain;
using Qbitflow.Core.Domain.SourceData;
using Qbitflow.Sources.Http;

namespace Qbitflow.Sources.Adapters;

/// <summary>
/// Jellystat does not have one stable, versioned public API contract at the time of
/// writing, so these are best-effort defaults for a typical self-hosted instance, not a
/// guarantee. If they don't match your deployment, override historyPath/resultsPath/
/// fieldMap in this instance's ExtraConfigJson -- no code change needed.
/// </summary>
public class JellystatAdapter(IInstanceHttpClientFactory httpClientFactory) : RestHistoryAdapterBase(httpClientFactory)
{
    public override SourceType SourceType => SourceType.Jellystat;

    protected override string DefaultHistoryPath => "/api/getHistory";
    protected override string DefaultResultsPath => "";

    protected override IReadOnlyDictionary<string, string> DefaultFieldMap => new Dictionary<string, string>
    {
        ["title"] = "NowPlayingItemName",
        ["filePath"] = "FullPath",
        ["user"] = "UserName",
        ["watchedAt"] = "ActivityDateInserted",
        ["percent"] = "PercentComplete"
    };

    protected override void ApplyAuth(HttpRequestMessage request, SourceConnectionInfo connection)
    {
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            request.Headers.Add("X-Api-Key", connection.ApiKey);
        }
    }
}
