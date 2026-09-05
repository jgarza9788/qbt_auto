using Qbitflow.Core.Domain;

namespace Qbitflow.Snapshot;

/// <summary>
/// Normalizes a raw file/save path into a stable join key, applying configured
/// PathMappingRules first so paths that differ only by container mount prefix collapse
/// to the same key. Done once at ingest (see SnapshotDatabase), never repeated at query time.
/// </summary>
public static class PathKeyNormalizer
{
    public static string? Normalize(string? rawPath, IReadOnlyList<PathMappingRule> rules)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var path = rawPath.Replace('\\', '/');

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            var from = rule.SourcePrefix.Replace('\\', '/');
            if (path.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                var to = rule.CanonicalPrefix.Replace('\\', '/').TrimEnd('/');
                var remainder = path[from.Length..].TrimStart('/');
                path = remainder.Length > 0 ? $"{to}/{remainder}" : to;
                break;
            }
        }

        path = path.TrimEnd('/');
        return path.ToLowerInvariant();
    }
}
