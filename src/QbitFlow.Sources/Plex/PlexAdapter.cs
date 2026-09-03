using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;

namespace QbitFlow.Sources.Plex;

public sealed record PlexConnectionInfo(
    Guid SourceId,
    Uri BaseUrl,
    SourceAuthMode AuthMode,
    string Username,
    string Secret,          // password (UserPassword) or a long-lived token (PlexToken)
    string ClientId);

/// <summary>
/// Plex media source. Refactor of the legacy <c>Utils/Plex.cs</c>: token via
/// <c>plex.tv/users/sign_in.json</c> (or a supplied token), XML library / history parsing.
/// Re-auths on a 401.
/// </summary>
public sealed class PlexAdapter(HttpClient http, PlexConnectionInfo info) : IMediaSourceAdapter
{
    private string _token = info.AuthMode == SourceAuthMode.PlexToken ? info.Secret : "";
    private readonly SemaphoreSlim _authGate = new(1, 1);

    public SourceKind Kind => SourceKind.Plex;
    public Guid SourceId => info.SourceId;

    public async Task<HealthResult> TestAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureTokenAsync(force: true, ct);
            _ = await GetXmlAsync("/library/sections", ct);
            return HealthResult.Healthy((int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return HealthResult.Unhealthy(ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<MediaRecord>> FetchMediaAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(force: false, ct);
        var records = new List<MediaRecord>();

        var sections = (await GetXmlAsync("/library/sections", ct))
            .Descendants("Directory")
            .Where(d => (string?)d.Attribute("type") is "movie" or "show")
            .Select(d => (Key: (string?)d.Attribute("key"), Type: (string?)d.Attribute("type")))
            .Where(s => s.Key is not null)
            .ToList();

        foreach (var section in sections)
        {
            ct.ThrowIfCancellationRequested();
            var items = (await GetXmlAsync($"/library/sections/{section.Key}/all", ct)).Descendants();

            foreach (var item in items)
            {
                var type = (string?)item.Attribute("type");
                if (type is not ("movie" or "show")) continue;

                var title = (string?)item.Attribute("title") ?? "Unknown";
                var year = ParseInt((string?)item.Attribute("year"));
                var rating = ParseDouble((string?)item.Attribute("rating") ?? (string?)item.Attribute("audienceRating"));
                var genres = item.Elements("Genre").Select(g => (string?)g.Attribute("tag") ?? "").Where(s => s.Length > 0).ToArray();
                var durationMs = ParseLong((string?)item.Attribute("duration"));
                var ratingKey = (string?)item.Attribute("ratingKey");

                IReadOnlyList<MediaFile> files;
                if (type == "movie")
                {
                    files = ParseParts(item);
                }
                else
                {
                    var eps = ratingKey is null ? null : await GetXmlAsync($"/library/metadata/{ratingKey}/allLeaves", ct);
                    files = eps is null ? [] : ParseParts(eps);
                }

                records.Add(new MediaRecord(ratingKey ?? title, title, type, year, rating, genres, durationMs, files));
            }
        }

        return records;
    }

    public async Task<IReadOnlyList<WatchRecord>> FetchWatchAsync(DateTimeOffset since, CancellationToken ct)
    {
        await EnsureTokenAsync(force: false, ct);

        var xml = await GetXmlAsync("/status/sessions/history/all?sort=viewedAt%3Adesc", ct);
        var sinceUnix = since.ToUnixTimeSeconds();

        var grouped = new Dictionary<string, (string Type, int Count, long LastViewed)>(StringComparer.OrdinalIgnoreCase);

        foreach (var v in xml.Descendants("Video"))
        {
            var viewedAt = ParseLong((string?)v.Attribute("viewedAt")) ?? 0;
            if (viewedAt <= 0 || viewedAt < sinceUnix) continue;

            var type = (string?)v.Attribute("type") ?? "movie";
            var title = type == "episode"
                ? (string?)v.Attribute("grandparentTitle") ?? "Unknown Show"
                : (string?)v.Attribute("title") ?? "Unknown Movie";

            if (grouped.TryGetValue(title, out var agg))
                grouped[title] = (agg.Type, agg.Count + 1, Math.Max(agg.LastViewed, viewedAt));
            else
                grouped[title] = (type == "episode" ? "show" : "movie", 1, viewedAt);
        }

        return grouped.Select(kv => new WatchRecord(
                kv.Key, kv.Key, kv.Value.Type, kv.Value.Count,
                DateTimeOffset.FromUnixTimeSeconds(kv.Value.LastViewed)))
            .ToArray();
    }

    // ---- HTTP / auth ----

    private async Task EnsureTokenAsync(bool force, CancellationToken ct)
    {
        if (info.AuthMode == SourceAuthMode.PlexToken) { _token = info.Secret; return; }
        if (_token.Length > 0 && !force) return;

        await _authGate.WaitAsync(ct);
        try
        {
            if (_token.Length > 0 && !force) return;

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://plex.tv/users/sign_in.json");
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{info.Username}:{info.Secret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            req.Headers.Add("X-Plex-Product", "qbit-flow");
            req.Headers.Add("X-Plex-Version", "1.0");
            req.Headers.Add("X-Plex-Client-Identifier", info.ClientId);
            req.Headers.Add("X-Plex-Platform", Environment.OSVersion.Platform.ToString());
            req.Headers.Add("X-Plex-Device", "server");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent("", Encoding.UTF8, "application/json");

            var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);
            _token = doc.RootElement.GetProperty("user").GetProperty("authToken").GetString() ?? "";
            if (_token.Length == 0) throw new InvalidOperationException("Plex sign-in returned no token.");
        }
        finally
        {
            _authGate.Release();
        }
    }

    private async Task<XElement> GetXmlAsync(string pathAndQuery, CancellationToken ct)
    {
        var url = BuildUrl(pathAndQuery);
        var res = await http.GetAsync(url, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized && info.AuthMode != SourceAuthMode.PlexToken)
        {
            await EnsureTokenAsync(force: true, ct);
            res = await http.GetAsync(BuildUrl(pathAndQuery), ct);
        }
        res.EnsureSuccessStatusCode();
        return XElement.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    private Uri BuildUrl(string pathAndQuery)
    {
        var sep = pathAndQuery.Contains('?') ? '&' : '?';
        return new Uri(info.BaseUrl, $"{pathAndQuery}{sep}X-Plex-Token={Uri.EscapeDataString(_token)}");
    }

    // ---- parsing helpers ----

    private static IReadOnlyList<MediaFile> ParseParts(XElement root) =>
        root.Descendants("Part")
            .Select(p => (string?)p.Attribute("file"))
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => new MediaFile(f!, System.IO.Path.GetFileName(f!), null))
            .ToArray();

    private static int? ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static long? ParseLong(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static double? ParseDouble(string? s) => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
}
