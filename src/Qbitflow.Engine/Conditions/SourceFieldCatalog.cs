using Qbitflow.Core.Domain;

namespace Qbitflow.Engine.Conditions;

/// <summary>
/// The documented, queryable surface of the snapshot schema, keyed by source type. This is the
/// single source of truth a condition's field key resolves against -- an author can never point a
/// condition at an arbitrary column, only at a <c>&lt;type&gt;.&lt;instance&gt;.&lt;field&gt;</c>
/// key listed here -- and it is also what the field reference panel introspects instead of
/// hand-maintaining a field list.
///
/// Every media/history source type shares one field set, because they share one table shape.
/// That is deliberate: which of those fields actually carry values for a given source is a
/// property of the adapter, not of the query language, and a field a source never populates
/// simply reads NULL rather than being a different key on every type.
/// </summary>
public static class SourceFieldCatalog
{
    public const string KindMedia = "media";
    public const string KindHistory = "history";

    public static readonly IReadOnlyDictionary<string, SourceTypeDefinition> Types = BuildTypes();

    /// <summary>SQLite user-defined functions callable from advanced-mode SQL (and used internally by computed fields).</summary>
    public static readonly IReadOnlyList<(string Signature, string Description)> Helpers =
    [
        ("days_since(timestamp)", "Days between now and an ISO-8601 timestamp column. NULL if the timestamp is NULL."),
        ("size_gb(bytes)", "Converts a byte count to gigabytes (decimal, 1e9). NULL if bytes is NULL."),
        ("path_matches(a, b)", "True if two normalized path_keys are equal, or one contains the other.")
    ];

    private static IReadOnlyDictionary<string, SourceTypeDefinition> BuildTypes()
    {
        var anchorKey = SourceNaming.TypeKey(SourceNaming.AnchorType);
        var types = new Dictionary<string, SourceTypeDefinition>(StringComparer.Ordinal)
        {
            [anchorKey] = new SourceTypeDefinition
            {
                TypeKey = anchorKey,
                DisplayName = "qBittorrent",
                TableName = anchorKey,
                AliasPrefix = "t",
                Correlation = SourceCorrelation.Anchor,
                Fields = TorrentFields()
            },
            [SourceNaming.StorageTypeKey] = new SourceTypeDefinition
            {
                TypeKey = SourceNaming.StorageTypeKey,
                DisplayName = "Storage",
                TableName = SourceNaming.StorageTypeKey,
                AliasPrefix = SourceNaming.StorageTypeKey,
                Correlation = SourceCorrelation.Standalone,
                Fields = StorageFields()
            }
        };

        // One shared field set across the six media/history types -- one table shape, one vocabulary.
        var mediaHistoryFields = MediaHistoryFields();
        foreach (var type in SourceNaming.MediaHistoryTypes)
        {
            var key = SourceNaming.TypeKey(type);
            types[key] = new SourceTypeDefinition
            {
                TypeKey = key,
                DisplayName = type.ToString(),
                TableName = key,
                // The type key itself: unique by construction, so adding a source type can never
                // collide with an existing alias, and the compiled-SQL preview stays readable.
                AliasPrefix = key,
                Correlation = SourceCorrelation.PathKey,
                Fields = mediaHistoryFields
            };
        }

        return types;
    }

    private static IReadOnlyDictionary<string, FieldDefinition> TorrentFields() => Index(
        Row("name", "{alias}.name", FieldValueType.Text, "The torrent's display name.", "Ubuntu 24.04 ISO"),
        Row("category", "{alias}.category", FieldValueType.Text, "qBittorrent category, if assigned.", "linux"),
        Row("tags", "{alias}.tags", FieldValueType.Text, "Comma-separated tag list; use Contains to match one tag.", "iso,verified"),
        Row("state", "{alias}.state", FieldValueType.Text, "qBittorrent status string.", "uploading"),
        Row("save_path", "{alias}.save_path", FieldValueType.Text, "Torrent's save location.", "/downloads"),
        Row("content_path", "{alias}.content_path", FieldValueType.Text, "Torrent's content path (file or folder).", "/downloads/Ubuntu.iso"),
        Row("size_bytes", "{alias}.size_bytes", FieldValueType.Integer, "Total size in bytes.", "5368709120"),
        Row("size_gb", "size_gb({alias}.size_bytes)", FieldValueType.Real, "Total size in GB.", "5.37"),
        Row("progress", "{alias}.progress", FieldValueType.Real, "Download progress, 0..1.", "1.0"),
        Row("ratio", "{alias}.ratio", FieldValueType.Real, "Upload/download ratio.", "0.42"),
        Row("downloaded_bytes", "{alias}.downloaded_bytes", FieldValueType.Integer, "Bytes downloaded.", "5368709120"),
        Row("uploaded_bytes", "{alias}.uploaded_bytes", FieldValueType.Integer, "Bytes uploaded.", "2147483648"),
        Row("upload_limit_bps", "{alias}.upload_limit_bps", FieldValueType.Integer, "Upload speed limit, bytes/sec (0 = unlimited).", "0"),
        Row("download_limit_bps", "{alias}.download_limit_bps", FieldValueType.Integer, "Download speed limit, bytes/sec (0 = unlimited).", "0"),
        Row("added_on", "{alias}.added_on", FieldValueType.DateTime, "When the torrent was added.", "2026-01-01T00:00:00+00:00"),
        Row("days_since_added", "days_since({alias}.added_on)", FieldValueType.Real, "Days since the torrent was added.", "42.5"),
        Row("completion_on", "{alias}.completion_on", FieldValueType.DateTime, "When the torrent finished downloading.", "2026-01-02T00:00:00+00:00"),
        Row("days_since_completed", "days_since({alias}.completion_on)", FieldValueType.Real, "Days since the torrent finished downloading.", "41.5"),
        Row("tracker", "{alias}.tracker", FieldValueType.Text, "Currently-working tracker URL (empty when none is working).", "udp://tracker.example.org:451/announce"),
        Row("total_size_bytes", "{alias}.total_size_bytes", FieldValueType.Integer, "Size of all selected files in bytes (>= size_bytes).", "8561604253"),
        Row("total_size_gb", "size_gb({alias}.total_size_bytes)", FieldValueType.Real, "Size of all selected files in GB.", "8.56"),
        Row("amount_left_bytes", "{alias}.amount_left_bytes", FieldValueType.Integer, "Bytes still to download (0 once complete).", "0"),
        Row("amount_left_gb", "size_gb({alias}.amount_left_bytes)", FieldValueType.Real, "GB still to download.", "0.0"),
        Row("completed_bytes", "{alias}.completed_bytes", FieldValueType.Integer, "Bytes of selected content already downloaded.", "8561604253"),
        Row("download_speed_bps", "{alias}.dl_speed_bps", FieldValueType.Integer, "Current download rate, bytes/sec.", "0"),
        Row("upload_speed_bps", "{alias}.up_speed_bps", FieldValueType.Integer, "Current upload rate, bytes/sec.", "1048576"),
        Row("eta_seconds", "{alias}.eta_seconds", FieldValueType.Integer, "Estimated seconds to completion (8640000 means qBittorrent reports no ETA).", "8640000"),
        Row("eta_hours", "{alias}.eta_seconds / 3600.0", FieldValueType.Real, "Estimated hours to completion.", "2400.0"),
        Row("seeding_time_seconds", "{alias}.seeding_time_seconds", FieldValueType.Integer, "Seconds spent seeding.", "62777820"),
        Row("seeding_days", "{alias}.seeding_time_seconds / 86400.0", FieldValueType.Real, "Days spent seeding.", "726.6"),
        Row("active_time_seconds", "{alias}.active_time_seconds", FieldValueType.Integer, "Seconds the torrent has been active (downloading or seeding).", "62828030"),
        Row("active_days", "{alias}.active_time_seconds / 86400.0", FieldValueType.Real, "Days the torrent has been active.", "727.2"),
        Row("connected_seeds", "{alias}.connected_seeds", FieldValueType.Integer, "Seeds currently connected to.", "3"),
        Row("total_seeds", "{alias}.total_seeds", FieldValueType.Integer, "Seeds in the swarm reported by the tracker.", "12"),
        Row("connected_leechers", "{alias}.connected_leechers", FieldValueType.Integer, "Leechers currently connected to.", "1"),
        Row("total_leechers", "{alias}.total_leechers", FieldValueType.Integer, "Leechers in the swarm reported by the tracker.", "50"),
        Row("availability", "{alias}.availability", FieldValueType.Real, "Fraction of the torrent available across peers; -1 when unknown.", "1.0"),
        Row("auto_tmm", "{alias}.auto_tmm", FieldValueType.Boolean, "Whether Automatic Torrent Management is enabled for this torrent.", "true"),
        Row("ratio_limit", "{alias}.ratio_limit", FieldValueType.Real, "Per-torrent share-ratio limit: -2 = use global, -1 = unlimited.", "-2.0"),
        Row("seeding_time_limit_minutes", "{alias}.seeding_time_limit_minutes", FieldValueType.Integer, "Per-torrent seeding-time limit in minutes: -2 = use global, -1 = unlimited.", "-2"),
        Row("last_activity", "{alias}.last_activity", FieldValueType.DateTime, "When the torrent last had tracker/peer activity.", "2026-07-27T06:44:34+00:00"),
        Row("days_since_activity", "days_since({alias}.last_activity)", FieldValueType.Real, "Days since the torrent last had activity.", "40.3"),
        Row("seen_complete", "{alias}.seen_complete", FieldValueType.DateTime, "When a complete copy was last seen in the swarm.", "2026-07-27T06:44:34+00:00"),
        Row("days_since_seen_complete", "days_since({alias}.seen_complete)", FieldValueType.Real, "Days since a complete copy was last seen in the swarm.", "40.3"));

    private static IReadOnlyDictionary<string, FieldDefinition> MediaHistoryFields() => Index(
        Row("kind", "{alias}.kind", FieldValueType.Text, "Which kind of row this is: 'media' (a library item) or 'history' (a playback event).", "history"),
        Row("title", "{alias}.title", FieldValueType.Text, "Item title, as reported by the source.", "Foo (2020)"),
        Row("file_path", "{alias}.file_path", FieldValueType.Text, "File path the source reported for this row.", "/media/movies/Foo.mkv"),

        RowOfKind("media_type", "{alias}.media_type", FieldValueType.Text, KindMedia, "movie, episode, etc. as reported by the source.", "movie"),
        RowOfKind("external_key", "{alias}.external_key", FieldValueType.Text, KindMedia, "The source's own id for the library item.", "12345"),
        RowOfKind("added_at", "{alias}.added_at", FieldValueType.DateTime, KindMedia, "When the media library added this item.", "2026-01-01T00:00:00+00:00"),
        RowOfKind("days_since_added", "days_since({alias}.added_at)", FieldValueType.Real, KindMedia, "Days since the media library added this item.", "42.5"),

        RowOfKind("user_name", "{alias}.user_name", FieldValueType.Text, KindHistory, "Viewer's username.", "alice"),
        RowOfKind("percent_complete", "{alias}.percent_complete", FieldValueType.Real, KindHistory, "Percent of the item watched, 0..100.", "95.0"),
        RowOfKind("watched_at", "{alias}.watched_at", FieldValueType.DateTime, KindHistory, "When this watch event occurred.", "2026-01-01T00:00:00+00:00"),
        RowOfKind("days_since_watched", "days_since({alias}.watched_at)", FieldValueType.Real, KindHistory, "Days since this watch event.", "10.2"),

        // Aggregates over every correlated row. These replace the old play_counts view, which
        // could only pool every history source together and had no way to name one instance.
        Agg("play_count", "COUNT(*)", null, FieldValueType.Integer, KindHistory, "How many times this torrent's content was watched. 0 when never watched.", "3"),
        Agg("distinct_viewers", "COUNT(DISTINCT {alias}.user_name)", null, FieldValueType.Integer, KindHistory, "How many different users watched it.", "2"),
        Agg("first_watched_at", "MIN({alias}.watched_at)", null, FieldValueType.DateTime, KindHistory, "Earliest watch event. NULL when never watched.", "2025-06-01T00:00:00+00:00"),
        Agg("last_watched_at", "MAX({alias}.watched_at)", null, FieldValueType.DateTime, KindHistory, "Most recent watch event. NULL when never watched.", "2026-01-01T00:00:00+00:00"),
        Agg("days_since_last_watched", "MAX({alias}.watched_at)", "days_since({inner})", FieldValueType.Real, KindHistory, "Days since the most recent watch event. NULL when never watched.", "10.2"),
        Agg("media_count", "COUNT(*)", null, FieldValueType.Integer, KindMedia, "How many library items point at this torrent's content. 0 when the library doesn't have it.", "1"));

    private static IReadOnlyDictionary<string, FieldDefinition> StorageFields() => Index(
        Row("total_bytes", "{alias}.total_bytes", FieldValueType.Integer, "Total capacity of the filesystem, in bytes.", "2000398934016"),
        Row("used_bytes", "{alias}.used_bytes", FieldValueType.Integer, "Bytes in use.", "1500299200512"),
        Row("free_bytes", "{alias}.free_bytes", FieldValueType.Integer, "Bytes free.", "500099733504"),
        Row("used_percent", "{alias}.used_percent", FieldValueType.Real, "Percent of capacity in use, 0..100.", "75.0"),
        Row("free_percent", "{alias}.free_percent", FieldValueType.Real, "Percent of capacity free, 0..100.", "25.0"),
        Row("free_gb", "size_gb({alias}.free_bytes)", FieldValueType.Real, "Free space in GB.", "500.1"),
        Row("used_gb", "size_gb({alias}.used_bytes)", FieldValueType.Real, "Used space in GB.", "1500.3"),
        Row("folder_size_gb", "size_gb({alias}.folder_size_bytes)", FieldValueType.Real, "Recursive size of the configured folder in GB, if scanned.", "820.4"),
        Row("available", "{alias}.available", FieldValueType.Boolean, "False when the path doesn't exist, isn't mounted, or couldn't be read.", "true"));

    private static FieldDefinition Row(string key, string expression, FieldValueType type, string description, string example) => new()
    {
        Key = key,
        RowExpression = expression,
        ValueType = type,
        Description = description,
        ExampleValue = example
    };

    private static FieldDefinition RowOfKind(string key, string expression, FieldValueType type, string kind, string description, string example) => new()
    {
        Key = key,
        RowExpression = expression,
        ValueType = type,
        KindFilter = kind,
        Description = description,
        ExampleValue = example
    };

    private static FieldDefinition Agg(string key, string aggregate, string? wrapper, FieldValueType type, string kind, string description, string example) => new()
    {
        Key = key,
        AggregateExpression = aggregate,
        AggregateWrapper = wrapper,
        ValueType = type,
        KindFilter = kind,
        Description = description,
        ExampleValue = example
    };

    private static IReadOnlyDictionary<string, FieldDefinition> Index(params FieldDefinition[] fields) =>
        fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
}
