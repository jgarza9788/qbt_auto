namespace Qbitflow.Snapshot;

/// <summary>Nullable-to-DBNull conversion helpers for parameterized inserts. Timestamps are stored as ISO-8601 text (sorts correctly, parseable by the days_since UDF).</summary>
internal static class DbValues
{
    public static object Of(string? value) => (object?)value ?? DBNull.Value;

    public static object Of(DateTimeOffset? value) => value.HasValue ? value.Value.ToString("o") : DBNull.Value;

    public static object Of(double? value) => value.HasValue ? value.Value : DBNull.Value;

    public static object Of(long? value) => value.HasValue ? value.Value : DBNull.Value;
}
