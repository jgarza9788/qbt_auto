namespace Qbitflow.Engine.Conditions;

/// <summary>
/// The documented, queryable surface of the snapshot schema. This is the single source
/// of truth a condition's Field key resolves against -- an author can never point a
/// condition at an arbitrary column, only at one of these keys -- and it's also what
/// Phase 7's field reference panel introspects instead of hand-maintaining a field list.
/// </summary>
public static class SnapshotFieldRegistry
{
    public static readonly IReadOnlyDictionary<string, RelationDefinition> Relations = new Dictionary<string, RelationDefinition>
    {
        ["torrents"] = new RelationDefinition
        {
            TableName = "torrents",
            AliasPrefix = "t",
            Fields = Field(
                F("name", "{alias}.name", FieldValueType.Text, "The torrent's display name.", "Ubuntu 24.04 ISO"),
                F("category", "{alias}.category", FieldValueType.Text, "qBittorrent category, if assigned.", "linux"),
                F("tags", "{alias}.tags", FieldValueType.Text, "Comma-separated tag list; use Contains to match one tag.", "iso,verified"),
                F("state", "{alias}.state", FieldValueType.Text, "qBittorrent status string.", "uploading"),
                F("save_path", "{alias}.save_path", FieldValueType.Text, "Torrent's save location.", "/downloads"),
                F("content_path", "{alias}.content_path", FieldValueType.Text, "Torrent's content path (file or folder).", "/downloads/Ubuntu.iso"),
                F("size_bytes", "{alias}.size_bytes", FieldValueType.Integer, "Total size in bytes.", "5368709120"),
                F("size_gb", "size_gb({alias}.size_bytes)", FieldValueType.Real, "Total size in GB.", "5.37"),
                F("progress", "{alias}.progress", FieldValueType.Real, "Download progress, 0..1.", "1.0"),
                F("ratio", "{alias}.ratio", FieldValueType.Real, "Upload/download ratio.", "0.42"),
                F("downloaded_bytes", "{alias}.downloaded_bytes", FieldValueType.Integer, "Bytes downloaded.", "5368709120"),
                F("uploaded_bytes", "{alias}.uploaded_bytes", FieldValueType.Integer, "Bytes uploaded.", "2147483648"),
                F("upload_limit_bps", "{alias}.upload_limit_bps", FieldValueType.Integer, "Upload speed limit, bytes/sec (0 = unlimited).", "0"),
                F("download_limit_bps", "{alias}.download_limit_bps", FieldValueType.Integer, "Download speed limit, bytes/sec (0 = unlimited).", "0"),
                F("added_on", "{alias}.added_on", FieldValueType.DateTime, "When the torrent was added.", "2026-01-01T00:00:00+00:00"),
                F("days_since_added", "days_since({alias}.added_on)", FieldValueType.Real, "Days since the torrent was added.", "42.5"),
                F("completion_on", "{alias}.completion_on", FieldValueType.DateTime, "When the torrent finished downloading.", "2026-01-02T00:00:00+00:00"),
                F("days_since_completed", "days_since({alias}.completion_on)", FieldValueType.Real, "Days since the torrent finished downloading.", "41.5"))
        },
        ["watch_history"] = new RelationDefinition
        {
            TableName = "watch_history",
            AliasPrefix = "wh",
            Fields = Field(
                F("media_title", "{alias}.media_title", FieldValueType.Text, "Title reported by the media/watch-history source.", "Foo (2020)"),
                F("user_name", "{alias}.user_name", FieldValueType.Text, "Viewer's username.", "alice"),
                F("percent_complete", "{alias}.percent_complete", FieldValueType.Real, "Percent of the item watched, 0..100.", "95.0"),
                F("watched_at", "{alias}.watched_at", FieldValueType.DateTime, "When this watch event occurred.", "2026-01-01T00:00:00+00:00"),
                F("days_since_watched", "days_since({alias}.watched_at)", FieldValueType.Real, "Days since this watch event.", "10.2"))
        },
        ["media_items"] = new RelationDefinition
        {
            TableName = "media_items",
            AliasPrefix = "mi",
            Fields = Field(
                F("title", "{alias}.title", FieldValueType.Text, "Media library item title.", "Foo (2020)"),
                F("media_type", "{alias}.media_type", FieldValueType.Text, "movie, episode, etc. as reported by the source.", "movie"),
                F("source_type", "{alias}.source_type", FieldValueType.Text, "Which kind of source this item came from.", "Plex"),
                F("added_at", "{alias}.added_at", FieldValueType.DateTime, "When the media library added this item.", "2026-01-01T00:00:00+00:00"),
                F("days_since_added", "days_since({alias}.added_at)", FieldValueType.Real, "Days since the media library added this item.", "42.5"))
        },
        ["play_counts"] = new RelationDefinition
        {
            TableName = "play_counts",
            AliasPrefix = "pc",
            Fields = Field(
                F("play_count", "{alias}.play_count", FieldValueType.Integer, "Total watch events for this path.", "3"),
                F("distinct_viewers", "{alias}.distinct_viewers", FieldValueType.Integer, "Distinct usernames that watched this path.", "2"),
                F("first_watched_at", "{alias}.first_watched_at", FieldValueType.DateTime, "Earliest watch event for this path.", "2025-06-01T00:00:00+00:00"),
                F("last_watched_at", "{alias}.last_watched_at", FieldValueType.DateTime, "Most recent watch event for this path.", "2026-01-01T00:00:00+00:00"),
                F("days_since_last_watched", "days_since({alias}.last_watched_at)", FieldValueType.Real, "Days since the most recent watch event.", "10.2"))
        }
    };

    /// <summary>Storage fields are accessed as "storage.&lt;path-name&gt;.&lt;attr&gt;" rather than through a relation -- see the attribute names here.</summary>
    public static readonly IReadOnlyDictionary<string, (string Column, FieldValueType ValueType)> StorageAttributes =
        new Dictionary<string, (string, FieldValueType)>
        {
            ["total_bytes"] = ("total_bytes", FieldValueType.Integer),
            ["used_bytes"] = ("used_bytes", FieldValueType.Integer),
            ["free_bytes"] = ("free_bytes", FieldValueType.Integer),
            ["used_percent"] = ("used_percent", FieldValueType.Real),
            ["free_percent"] = ("free_percent", FieldValueType.Real),
            ["free_gb"] = ("size_gb(free_bytes)", FieldValueType.Real),
            ["used_gb"] = ("size_gb(used_bytes)", FieldValueType.Real),
            ["folder_size_gb"] = ("size_gb(folder_size_bytes)", FieldValueType.Real),
            ["available"] = ("available", FieldValueType.Boolean)
        };

    /// <summary>SQLite user-defined functions callable from advanced-mode SQL (and used internally by computed fields above).</summary>
    public static readonly IReadOnlyList<(string Signature, string Description)> Helpers =
    [
        ("days_since(timestamp)", "Days between now and an ISO-8601 timestamp column. NULL if the timestamp is NULL."),
        ("size_gb(bytes)", "Converts a byte count to gigabytes (decimal, 1e9). NULL if bytes is NULL."),
        ("path_matches(a, b)", "True if two normalized path_keys are equal, or one contains the other. Used to correlate EXISTS subqueries by path_key.")
    ];

    private static FieldDefinition F(string key, string sqlExpression, FieldValueType type, string description, string example) => new()
    {
        Key = key,
        SqlExpression = sqlExpression,
        ValueType = type,
        Description = description,
        ExampleValue = example
    };

    private static IReadOnlyDictionary<string, FieldDefinition> Field(params FieldDefinition[] fields) =>
        fields.ToDictionary(f => f.Key);
}
