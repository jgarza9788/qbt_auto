using System.Text.Json;

namespace QbitFlow.Engine.Analytics;

/// <summary>
/// Recency weights applied to per-window watch counts. Matches the legacy defaults
/// (all 0.01 / year 0.5 / month 0.9 / week 1.0). A flat <c>PlayCount</c> with no window is
/// treated as the "all" window.
/// </summary>
public sealed record AnalyticsWeights(double All, double Year, double Month, double Week)
{
    public static readonly AnalyticsWeights Default = new(0.01, 0.50, 0.90, 1.00);

    public double For(string window) => window.ToLowerInvariant() switch
    {
        "week" => Week,
        "month" => Month,
        "year" => Year,
        _ => All,
    };

    public static AnalyticsWeights Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            double Get(string k, double d) => r.TryGetProperty(k, out var v) && v.TryGetDouble(out var x) ? x : d;
            return new AnalyticsWeights(
                Get("all", Default.All), Get("year", Default.Year),
                Get("month", Default.Month), Get("week", Default.Week));
        }
        catch
        {
            return Default;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new { all = All, year = Year, month = Month, week = Week });
}
