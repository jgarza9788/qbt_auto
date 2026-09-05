using Qbitflow.Core.Domain;
using Qbitflow.Sources.Http;

namespace Qbitflow.Sources.Adapters;

/// <summary>Tautulli's /api/v2 (cmd=get_history) is a documented, stable API -- these defaults should work unmodified for most installs.</summary>
public class TautulliAdapter(IInstanceHttpClientFactory httpClientFactory) : RestHistoryAdapterBase(httpClientFactory)
{
    public override SourceType SourceType => SourceType.Tautulli;

    protected override string DefaultHistoryPath => "/api/v2?apikey={apiKey}&cmd=get_history&length=1000";
    protected override string DefaultResultsPath => "response.data.data";

    protected override IReadOnlyDictionary<string, string> DefaultFieldMap => new Dictionary<string, string>
    {
        ["title"] = "full_title",
        ["filePath"] = "file",
        ["user"] = "friendly_name",
        ["watchedAt"] = "date",
        ["percent"] = "percent_complete"
    };
}
