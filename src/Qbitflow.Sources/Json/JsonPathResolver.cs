using System.Text.Json;

namespace Qbitflow.Sources.Json;

/// <summary>Minimal dot-path traversal of a JsonElement tree, used to make the REST-history adapters' response shape configurable.</summary>
internal static class JsonPathResolver
{
    public static JsonElement? Resolve(JsonElement root, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return root;
        }

        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }
            current = next;
        }

        return current;
    }

    public static string? GetString(JsonElement element, string? fieldName)
    {
        if (string.IsNullOrEmpty(fieldName) || element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(fieldName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    public static double? GetDouble(JsonElement element, string? fieldName)
    {
        if (string.IsNullOrEmpty(fieldName) || element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(fieldName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), out var d) => d,
            _ => null
        };
    }

    public static DateTimeOffset? GetUnixSeconds(JsonElement element, string? fieldName)
    {
        var value = GetDouble(element, fieldName);
        return value is > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)value.Value) : null;
    }
}
