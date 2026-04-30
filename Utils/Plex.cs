/*
 * ============================================================================
 *  Project:      qbt_auto
 *  File:         Plex.cs
 *  Description:  see Readme.md and comments
 *
 *  Author:       Justin Garza
 *  Created:      2025-09-06
 *
 *  License:      Usage of this file is governed by the LICENSE file at the
 *                root of this repository. See the LICENSE file for details.
 *
 *  Copyright:    (c) 2025 Justin Garza. All rights reserved.
 * ============================================================================
 */

using System.Xml.Linq;
using Json5Core;
using NLog;


namespace Utils
{

    public class PlexLibrary
    {
        public string? Key { get; set; }
        public string? Title { get; set; }
        public string? Type { get; set; }
        public override string ToString() => $"{Title} ({Type}) [Key={Key}]";
    }

    public class PlexPlay
    {
        public string MediaTitle { get; set; } = "";
        public string VideoType  { get; set; } = "";
        public long   ViewedAt   { get; set; } = -1;
        public string AccountID  { get; set; } = "";
        public string DeviceID   { get; set; } = "";
    }

    public record NormalizedPlexPlays(
        List<(string Title, double Score)> Movies,
        List<(string Title, double Score)> Shows
    );

    class Plex
    {
        private static readonly Logger logger = LogManager.GetLogger("LoggerF");

        public string baseUrl = "";

        public string client_id = "";
        public string user    = "";
        public string pwd     = "";
        public string token   = "";

        private readonly HttpClient http = new();

        public List<PlexLibrary> plexLibraries { get; private set; } = new();

        // keyed by full file path (ContentPath)
        public Dictionary<string, object> items = new();
        public Dictionary<string, object>? items_grouped = new(); 

        public string basePath  = Directory.GetCurrentDirectory();
        public string cacheName = "plex_cache.json";
        private string cacheFile = "";

        public bool   isLoaded  = false;
        public double ageInDays = 0.0;

        public Plex(string iBaseUrl, string iuser, string ipassword, bool loadCacheFile = false)
        {
            baseUrl  = iBaseUrl;
            user     = iuser;
            pwd      = ipassword;
            cacheFile = Path.Combine(basePath, cacheName);
            if (loadCacheFile) loadCache();
        }

        public Plex(bool loadCacheFile = false)
        {
            cacheFile = Path.Combine(basePath, cacheName);
            if (loadCacheFile) loadCache();
        }

        public void loadCache()
        {
            if (!File.Exists(cacheFile)) return;

            var fi = new FileInfo(cacheFile);
            ageInDays = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalDays;

            if (ageInDays < 1.0)
            {
                string content = File.ReadAllText(cacheFile);
                items    = Json5.Deserialize<Dictionary<string, object>>(content) ?? new();
                items_grouped ??= items
                    .GroupBy(e => e.Key.Split('/').Last())
                    .ToDictionary(g => g.Key, g => g.First().Value);

                isLoaded = true;
            }
        }



        public async Task GetTokenAsync()
        {
            var req   = new HttpRequestMessage(HttpMethod.Post, "https://plex.tv/users/sign_in.json");
            var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{pwd}"));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
            req.Headers.Add("X-Plex-Product",           "qbt_auto");
            req.Headers.Add("X-Plex-Version",           "1.0");
            req.Headers.Add("X-Plex-Client-Identifier", client_id);
            req.Headers.Add("X-Plex-Platform",          "Windows");
            req.Headers.Add("X-Plex-Device",            "PC");
            req.Headers.Add("Accept",                   "application/json");
            req.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");

            var res  = await http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                logger.Error("GetTokenAsync failed {StatusCode}: {Body}", (int)res.StatusCode, body);
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            token = doc.RootElement.GetProperty("user").GetProperty("authToken").GetString() ?? "";
        }

        public double GetNormalizedScore(string name, List<(string Title, double Score)> list)
        {
            var match = list.FirstOrDefault(x => x.Title.Equals(name, StringComparison.OrdinalIgnoreCase));
            return match == default ? 0.0 : match.Score;
        }

        private async Task FetchLibrariesAsync()
        {
            var xml = await http.GetStringAsync($"{baseUrl}/library/sections?X-Plex-Token={token}");
            plexLibraries = XDocument.Parse(xml).Descendants("Directory")
                .Where(n => (string?)n.Attribute("type") is "movie" or "show")
                .Select(d => new PlexLibrary
                {
                    Key   = (string?)d.Attribute("key"),
                    Title = (string?)d.Attribute("title"),
                    Type  = (string?)d.Attribute("type")
                })
                .ToList();
        }

        private async Task<List<PlexPlay>> GetPlexPlaysAsync(string period = "year")
        {
            try
            {
                var xml = await http.GetStringAsync(
                    $"{baseUrl}/status/sessions/history/all?activeTimePeriod={period}&X-Plex-Token={token}&sort=viewedAt%3Adesc");

                return XDocument.Parse(xml).Descendants("Video")
                    .Select(d => new PlexPlay
                    {
                        MediaTitle = d.Attribute("type")?.Value == "episode"
                            ? (string?)d.Attribute("grandparentTitle") ?? "Unknown Show"
                            : (string?)d.Attribute("title")            ?? "Unknown Movie",
                        ViewedAt  = long.TryParse(d.Attribute("viewedAt")?.Value, out var ts) ? ts : 0L,
                        VideoType = (string?)d.Attribute("type")      ?? "unknown",
                        AccountID = (string?)d.Attribute("accountID") ?? "unknown",
                        DeviceID  = (string?)d.Attribute("deviceID")  ?? "unknown"
                    })
                    .Where(x => x.ViewedAt > 0)
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "issue getting plays for period {period}", period);
                return [];
            }
        }

        /// <summary>
        /// Returns a normalized 0–1 play-score list for movies and shows, using
        /// weighted counts across four time windows (all/year/month/week).
        /// </summary>
        private async Task<NormalizedPlexPlays> GetNormalizedPlexPlaysAsync()
        {
            var periods = new (string Name, float Weight)[]
            {
                ("all",   0.10f),
                ("year",  0.50f),
                ("month", 0.75f),
                ("week",  1.00f)
            };

            // Fetch all periods in parallel
            var results = await Task.WhenAll(periods.Select(p => GetPlexPlaysAsync(p.Name)));

            var movieCounts = new Dictionary<string, float>();
            var showCounts  = new Dictionary<string, float>();

            for (int i = 0; i < periods.Length; i++)
            {
                float weight = periods[i].Weight;

                foreach (var m in results[i].Where(x => x.VideoType == "movie"))
                    movieCounts[m.MediaTitle] = movieCounts.GetValueOrDefault(m.MediaTitle, 0f) + weight;

                foreach (var s in results[i].Where(x => x.VideoType == "episode"))
                    showCounts[s.MediaTitle] = showCounts.GetValueOrDefault(s.MediaTitle, 0f) + weight;
            }

            return new NormalizedPlexPlays(
                Movies: Misc.NormalizeQuantile(movieCounts),
                Shows:  Misc.NormalizeQuantile(showCounts)
            );
        }

        private static Dictionary<string, object> BuildItemDict(
            string? title,         string? contentRating,    string? contentRatingAge,
            string? summary,       string? rating,           string? audienceRating,
            string? userRating,    string? viewCount,        double  nview,
            string? lastViewedAt,  string? lastRatedAt,      string? year,
            string? duration,      string  tags,             List<string?> tagList,
            string  files,         object  fileList,         string? ratingKey,
            string? type)
        {
            return new Dictionary<string, object>
            {
                ["plex_title"]            = title            ?? "null",
                ["plex_name"]             = title            ?? "null",
                ["plex_contentRating"]    = contentRating    ?? "0",
                ["plex_contentRatingAge"] = contentRatingAge ?? "0",
                ["plex_summary"]          = summary          ?? "null",
                ["plex_rating"]           = rating           ?? "0",
                ["plex_audienceRating"]   = audienceRating   ?? "0",
                ["plex_userRating"]       = userRating       ?? "0",
                ["plex_viewCount"]        = viewCount        ?? "0",
                ["plex_nview"]            = nview,
                ["plex_lastViewedAt"]     = lastViewedAt     ?? "0",
                ["plex_lastRatedAt"]      = lastRatedAt      ?? "0",
                ["plex_year"]             = year             ?? "0",
                ["plex_duration"]         = duration         ?? "0",
                ["plex_tags"]             = tags,
                ["plex_tagList"]          = tagList,
                ["plex_files"]            = files,
                ["plex_fileList"]         = fileList,
                ["plex_ratingKey"]        = ratingKey        ?? "-1",
                ["plex_type"]             = type             ?? "null",
            };
        }

        public async Task LoadAsync(bool forceReload = false)
        {
            if (isLoaded && !forceReload) return;

            try { await GetTokenAsync(); }
            catch (Exception ex) { logger.Error(ex, "issue getting token"); }

            if (token == "")
            {
                isLoaded = false;
                return;
            }

            try
            {
                await FetchLibrariesAsync();
                var npp = await GetNormalizedPlexPlaysAsync();

                foreach (var pl in plexLibraries)
                {
                    var itemsXml = await http.GetStringAsync(
                        $"{baseUrl}/library/sections/{pl.Key}/all?X-Plex-Token={token}");
                    var mediaItems = XDocument.Parse(itemsXml).Descendants()
                        .Select(v => new
                        {
                            title            = (string?)v.Attribute("title"),
                            contentRating    = (string?)v.Attribute("contentRating"),
                            contentRatingAge = (string?)v.Attribute("contentRatingAge"),
                            summary          = (string?)v.Attribute("summary"),
                            rating           = (string?)v.Attribute("rating"),
                            audienceRating   = (string?)v.Attribute("audienceRating"),
                            userRating       = (string?)v.Attribute("userRating"),
                            viewCount        = (string?)v.Attribute("viewCount"),
                            NView            = pl.Type == "movie"
                                ? GetNormalizedScore((string?)v.Attribute("title") ?? "", npp.Movies)
                                : GetNormalizedScore((string?)v.Attribute("title") ?? "", npp.Shows),
                            lastViewedAt     = (string?)v.Attribute("lastViewedAt"),
                            lastRatedAt      = (string?)v.Attribute("lastRatedAt"),
                            year             = (string?)v.Attribute("year"),
                            duration         = (string?)v.Attribute("duration"),
                            tags             = string.Join(";", v.Descendants("Genre")
                                                  .Select(t => (string?)t.Attribute("tag"))),
                            tagList          = v.Descendants("Genre")
                                                  .Select(t => (string?)t.Attribute("tag"))
                                                  .ToList(),
                            files            = string.Join(";", v.Descendants("Part")
                                                  .Select(p => (string?)p.Attribute("file"))
                                                  .Where(f => !string.IsNullOrEmpty(f))),
                            fileList         = v.Descendants("Part")
                                                  .Select(p => (string?)p.Attribute("file"))
                                                  .Where(f => !string.IsNullOrEmpty(f))
                                                  .ToList(),
                            ratingKey        = (string?)v.Attribute("ratingKey"),
                            type             = (string?)v.Attribute("type")
                        });

                    foreach (var x in mediaItems)
                    {
                        if (x.type == "movie")
                        {
                            foreach (var f in x.fileList)
                            {
                                if (f == null) continue;
                                items[f] = BuildItemDict(
                                    x.title, x.contentRating, x.contentRatingAge,
                                    x.summary, x.rating, x.audienceRating, x.userRating, x.viewCount,
                                    x.NView, x.lastViewedAt, x.lastRatedAt, x.year, x.duration,
                                    x.tags, x.tagList, x.files, x.fileList, x.ratingKey, x.type);
                            }
                        }
                        else if (x.type == "show")
                        {
                            var epsXml = await http.GetStringAsync(
                                $"{baseUrl}/library/metadata/{x.ratingKey}/allLeaves?X-Plex-Token={token}");
                            var epFiles = XDocument.Parse(epsXml)
                                .Descendants("Part")
                                .Select(v => (string?)v.Attribute("file"))
                                .Where(f => f != null);

                            foreach (var f in epFiles)
                            {
                                items[f!] = BuildItemDict(
                                    x.title, x.contentRating, x.contentRatingAge,
                                    x.summary, x.rating, x.audienceRating, x.userRating, x.viewCount,
                                    x.NView, x.lastViewedAt, x.lastRatedAt, x.year, x.duration,
                                    x.tags, x.tagList, f!, new List<string?> { f }, x.ratingKey, x.type);
                            }
                        }
                    }
                }

                File.WriteAllText(cacheFile, Json5.Serialize(items));

                items_grouped ??= items
                    .GroupBy(e => e.Key.Split('/').Last())
                    .ToDictionary(g => g.Key, g => g.First().Value);

                ageInDays = 0.0;
                isLoaded  = true;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                isLoaded = false;
            }
        }

        public Dictionary<string, object> getData(string contentPath)
        {
            return getDataByContentPath(contentPath);
        }

        /// <summary>
        /// Returns the Plex metadata dict for the given file path.
        /// Falls back to prefix-match if no exact key is found.
        /// </summary>
        public Dictionary<string, object> getDataByContentPath(string path)
        {
            string _cp = path.Split('/').Last();

            // logger.Info(_cp);
            // foreach (var entry in items)
            // {
            //     logger.Info($"{entry.Key} | {entry.Value}");
            // }
            
            if (items_grouped != null && items_grouped.TryGetValue(_cp, out var val))
            {
                return val as Dictionary<string, object> ?? new();
            }

            foreach (var entry in items)
            {
                if (entry.Key.Contains(_cp))
                {
                    return entry.Value as Dictionary<string, object> ?? new(); 
                }
            }
            return new();
        }

        /// //old version
        /*
        public Dictionary<string, object> getDataByContentPath(string path)
        {
            if (items.TryGetValue(path, out var value))
            {
                return value as Dictionary<string, object> ?? new();
            }

            foreach (var entry in items)
            {
                if (entry.Key.StartsWith(path))
                    return entry.Value as Dictionary<string, object> ?? new();
            }
            return new();
        }
        */

    }

}
