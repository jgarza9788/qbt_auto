using System.Text.Json;

namespace Qbitflow.Sources.Adapters;

/// <summary>
/// Per-instance overrides for a RestHistoryAdapterBase subclass, parsed from
/// Instance.ExtraConfigJson, e.g.:
/// <code>{ "historyPath": "/api/v2?apikey={apiKey}&amp;cmd=get_history", "resultsPath": "response.data.data",
/// "fieldMap": { "title": "full_title", "watchedAt": "date" } }</code>
/// Lets a user point an adapter at a differently-shaped deployment without a code change.
/// </summary>
public class RestHistoryConfig
{
    public required string HistoryPath { get; init; }
    public required string ResultsPath { get; init; }
    public required Dictionary<string, string> FieldMap { get; init; }

    public static RestHistoryConfig Parse(
        string? extraConfigJson,
        string defaultHistoryPath,
        string defaultResultsPath,
        IReadOnlyDictionary<string, string> defaultFieldMap)
    {
        var fieldMap = new Dictionary<string, string>(defaultFieldMap);
        var historyPath = defaultHistoryPath;
        var resultsPath = defaultResultsPath;

        if (!string.IsNullOrWhiteSpace(extraConfigJson))
        {
            using var doc = JsonDocument.Parse(extraConfigJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("historyPath", out var hp) && hp.ValueKind == JsonValueKind.String)
            {
                historyPath = hp.GetString() ?? historyPath;
            }
            if (root.TryGetProperty("resultsPath", out var rp) && rp.ValueKind == JsonValueKind.String)
            {
                resultsPath = rp.GetString() ?? resultsPath;
            }
            if (root.TryGetProperty("fieldMap", out var fm) && fm.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in fm.EnumerateObject())
                {
                    fieldMap[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
        }

        return new RestHistoryConfig { HistoryPath = historyPath, ResultsPath = resultsPath, FieldMap = fieldMap };
    }
}
