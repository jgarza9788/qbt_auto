using QbitFlow.Core.Domain;

namespace QbitFlow.Core.Expressions;

public sealed record FieldDef(
    string Key,
    FieldType Type,
    FieldSource Source,
    string Description,
    string Sample)
{
    public string Placeholder => $"<{Key}>";
}

/// <summary>
/// The set of <c>&lt;placeholder&gt;</c> fields surfaced to the rule builder, the raw-expression
/// validator, and the docs. Drive fields are dynamic (per mount) and not enumerated here.
/// </summary>
public static class FieldCatalog
{
    public static readonly IReadOnlyList<FieldDef> All =
    [
        // ---- Torrent ----
        new("Name",              FieldType.String,  FieldSource.Torrent, "Torrent name", "Some.Show.S01E01.1080p"),
        new("Category",          FieldType.String,  FieldSource.Torrent, "qBittorrent category", "Movies"),
        new("Tags",              FieldType.List,    FieldSource.Torrent, "Comma-joined tags", "hd,seed"),
        new("SavePath",          FieldType.String,  FieldSource.Torrent, "Save path", "/media/Movies"),
        new("ContentPath",       FieldType.String,  FieldSource.Torrent, "Content path", "/media/Movies/Film.mkv"),
        new("State",             FieldType.String,  FieldSource.Torrent, "Torrent state", "uploading"),
        new("Size",              FieldType.Number,  FieldSource.Torrent, "Total size in bytes", "1073741824"),
        new("Downloaded",        FieldType.Number,  FieldSource.Torrent, "Bytes downloaded", "1073741824"),
        new("Uploaded",          FieldType.Number,  FieldSource.Torrent, "Bytes uploaded", "5368709120"),
        new("Progress",          FieldType.Number,  FieldSource.Torrent, "0..1 complete", "1"),
        new("Ratio",             FieldType.Number,  FieldSource.Torrent, "Share ratio", "2.5"),
        new("NumSeeds",          FieldType.Number,  FieldSource.Torrent, "Connected seeds", "3"),
        new("NumLeechs",         FieldType.Number,  FieldSource.Torrent, "Connected leechers", "1"),
        new("DownloadLimit",     FieldType.Number,  FieldSource.Torrent, "Per-torrent DL limit (B/s)", "0"),
        new("UploadLimit",       FieldType.Number,  FieldSource.Torrent, "Per-torrent UL limit (B/s)", "0"),
        new("ActiveTime",        FieldType.Number,  FieldSource.Torrent, "Active time in .NET ticks (legacy parity)", "12096000000000"),
        new("ActiveTimeSeconds", FieldType.Number,  FieldSource.Torrent, "Seconds active", "1209600"),
        new("ActiveTimeDays",    FieldType.Number,  FieldSource.Torrent, "Days active", "14"),
        new("AddedOn",           FieldType.DateIso, FieldSource.Torrent, "When added (ISO)", "2026-01-01T00:00:00Z"),
        new("CompletionOn",      FieldType.DateIso, FieldSource.Torrent, "When completed (ISO)", "2026-01-02T00:00:00Z"),
        new("LastActivityTime",  FieldType.DateIso, FieldSource.Torrent, "Last activity (ISO)", "2026-02-01T00:00:00Z"),
        new("LastSeenComplete",  FieldType.DateIso, FieldSource.Torrent, "Last seen complete (ISO)", "2026-02-01T00:00:00Z"),
        new("ForceStart",        FieldType.Bool,    FieldSource.Torrent, "Force-start enabled", "False"),
        new("AutoManaged",       FieldType.Bool,    FieldSource.Torrent, "Automatic torrent management", "True"),

        // ---- Media (canonical + legacy plex_* aliases) ----
        new("media_title",       FieldType.String,  FieldSource.Media, "Matched media title", "The Film"),
        new("media_year",        FieldType.Number,  FieldSource.Media, "Release year", "2024"),
        new("media_rating",      FieldType.Number,  FieldSource.Media, "Critic/community rating", "7.8"),
        new("media_genres",      FieldType.String,  FieldSource.Media, "Genres (csv)", "Action,Sci-Fi"),
        new("media_type",        FieldType.String,  FieldSource.Media, "movie | show | episode", "movie"),
        new("media_duration_ms", FieldType.Number,  FieldSource.Media, "Runtime (ms)", "7200000"),
        new("plex_title",        FieldType.String,  FieldSource.Media, "Alias of media_title", "The Film"),
        new("plex_year",         FieldType.Number,  FieldSource.Media, "Alias of media_year", "2024"),
        new("plex_rating",       FieldType.Number,  FieldSource.Media, "Alias of media_rating", "7.8"),
        new("plex_viewCount",    FieldType.Number,  FieldSource.Media, "Aggregated play count", "4"),
        new("plex_nview",        FieldType.Number,  FieldSource.Derived, "Alias of watch_popularity", "0.62"),

        // ---- Derived ----
        new("watch_popularity",       FieldType.Number, FieldSource.Derived, "0..1 — matched media's watch total, ranked within the qBt category", "0.62"),
        new("hotcold",                FieldType.Number, FieldSource.Derived, "Legacy alias of watch_popularity", "0.62"),
        new("watch_total",            FieldType.Number, FieldSource.Derived, "Weighted watch total across all sources", "3.4"),
        new("days_since_last_watched",FieldType.Number, FieldSource.Derived, "Days since last watched (99999 if never)", "45"),
        new("is_media_matched",       FieldType.Bool,   FieldSource.Derived, "Torrent matched a media item", "True"),
    ];

    public static readonly IReadOnlyDictionary<string, FieldDef> ByKey =
        All.ToDictionary(f => f.Key, StringComparer.Ordinal);

    public static FieldType TypeOf(string key) =>
        ByKey.TryGetValue(key, out var def) ? def.Type : FieldType.String;
}
