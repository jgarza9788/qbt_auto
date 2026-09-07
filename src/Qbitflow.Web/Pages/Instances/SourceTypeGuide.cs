using Qbitflow.Core.Domain;

namespace Qbitflow.Web.Pages.Instances;

/// <summary>The instance form's help text for one source type.</summary>
public sealed record SourceFieldGuide
{
    /// <summary>Shown in the source-type dropdown.</summary>
    public required string Label { get; init; }

    public required string BaseUrlPlaceholder { get; init; }
    public required string BaseUrl { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required string ApiKey { get; init; }
    public required string ExtraConfig { get; init; }
    public string ExtraConfigPlaceholder { get; init; } = "";
}

/// <summary>
/// Per-source-type help text for the add/edit instance form, so the hints describe the type that
/// is actually selected rather than listing every type at once.
///
/// This is presentation copy, which is why it lives beside the page rather than next to the
/// adapters -- but it does have to stay true to them, so
/// <c>SourceTypeGuideTests</c> asserts every <see cref="SourceType"/> has an entry.
/// </summary>
public static class SourceTypeGuide
{
    private const string RestHistoryExtraConfig =
        "Optional overrides for a differently-shaped deployment: historyPath, resultsPath, fieldMap.";

    private const string BestEffortExtraConfig =
        "This source has no single stable API, so the shipped defaults are a starting point. "
        + "Override historyPath, resultsPath or fieldMap here if yours differs -- no code change needed.";

    public static readonly IReadOnlyDictionary<SourceType, SourceFieldGuide> ForType =
        new Dictionary<SourceType, SourceFieldGuide>
        {
            [SourceType.Qbittorrent] = new SourceFieldGuide
            {
                Label = "qBittorrent",
                BaseUrlPlaceholder = "http://192.168.1.10:8080",
                BaseUrl = "The qBittorrent WebUI address.",
                Username = "WebUI username. Leave blank if you have bypassed authentication for qbitflow's address.",
                Password = "WebUI password.",
                ApiKey = "Not used by qBittorrent -- it logs in with the username and password above.",
                ExtraConfig = "Not used by qBittorrent."
            },
            [SourceType.Plex] = new SourceFieldGuide
            {
                Label = "Plex",
                BaseUrlPlaceholder = "http://192.168.1.10:32400",
                BaseUrl = "Your Plex Media Server address.",
                Username = "Not used by Plex.",
                Password = "Not used by Plex.",
                ApiKey = "Your Plex token, sent as the X-Plex-Token header.",
                ExtraConfig = "Not used by Plex."
            },
            [SourceType.Jellyfin] = new SourceFieldGuide
            {
                Label = "Jellyfin",
                BaseUrlPlaceholder = "http://192.168.1.10:8096",
                BaseUrl = "Your Jellyfin server address.",
                Username = "Not used by Jellyfin.",
                Password = "Not used by Jellyfin.",
                ApiKey = "An API key from Jellyfin's Dashboard -> API Keys, sent as Authorization: MediaBrowser Token.",
                ExtraConfig = "Not used by Jellyfin."
            },
            [SourceType.Tautulli] = new SourceFieldGuide
            {
                Label = "Tautulli",
                BaseUrlPlaceholder = "http://192.168.1.10:8181",
                BaseUrl = "Your Tautulli address.",
                Username = "Not used by Tautulli.",
                Password = "Not used by Tautulli.",
                ApiKey = "Tautulli's Settings -> Web Interface -> API key, sent as the apikey query parameter.",
                ExtraConfig = RestHistoryExtraConfig,
                ExtraConfigPlaceholder = """{"fieldMap": {"title": "full_title"}}"""
            },
            [SourceType.Jellystat] = new SourceFieldGuide
            {
                Label = "Jellystat",
                BaseUrlPlaceholder = "http://192.168.1.10:3000",
                BaseUrl = "Your Jellystat address.",
                Username = "Not used by Jellystat.",
                Password = "Not used by Jellystat.",
                ApiKey = "Your Jellystat API key, sent as the X-Api-Key header.",
                ExtraConfig = BestEffortExtraConfig,
                ExtraConfigPlaceholder = """{"historyPath": "/api/getHistory"}"""
            },
            [SourceType.Jellyglance] = new SourceFieldGuide
            {
                Label = "Jellyglance",
                BaseUrlPlaceholder = "http://192.168.1.10:3000",
                BaseUrl = "Your Jellyglance address.",
                Username = "Not used by Jellyglance.",
                Password = "Not used by Jellyglance.",
                ApiKey = "Your Jellyglance API key, sent as the X-Api-Key header.",
                ExtraConfig = BestEffortExtraConfig,
                ExtraConfigPlaceholder = """{"historyPath": "/api/history"}"""
            }
        };

    public static SourceFieldGuide For(SourceType type) => ForType[type];
}
