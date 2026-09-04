namespace QbitFlow.Web.Pages;

/// <summary>
/// Maps the friendly "CPU speed" tiers shown in Settings to the raw max-parallelism integer
/// (<see cref="QbitFlow.Core.Domain.AppSetting.EngineMaxParallelism"/>). A stored value that is not
/// exactly one of the tier values simply displays as the nearest tier.
/// </summary>
public static class CpuSpeedMap
{
    public static readonly IReadOnlyList<(string Label, int Value)> Tiers =
    [
        ("Low", 2),
        ("Medium", 8),
        ("High", 16),
        ("SuperHigh", 32),
    ];

    public const string DefaultLabel = "Medium";
    private const int DefaultValue = 8;

    /// <summary>Exact label match (case-insensitive), else the default (8).</summary>
    public static int ToValue(string? label)
    {
        foreach (var (name, value) in Tiers)
            if (string.Equals(name, label, StringComparison.OrdinalIgnoreCase))
                return value;
        return DefaultValue;
    }

    /// <summary>Nearest tier by absolute distance.</summary>
    public static string ToLabel(int value)
    {
        var best = Tiers[0];
        var bestDist = Math.Abs(value - best.Value);
        foreach (var tier in Tiers.Skip(1))
        {
            var dist = Math.Abs(value - tier.Value);
            if (dist < bestDist) (best, bestDist) = (tier, dist);
        }
        return best.Label;
    }
}
