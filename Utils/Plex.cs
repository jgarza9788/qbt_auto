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

using System.Collections.Immutable;
using System.Net.Http;
using System.Xml;
using System.Xml.Linq;
using NLog.Targets.Wrappers;
using System.Diagnostics;
using Json5Core;
using NLog;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis.CSharp.Syntax;


namespace Utils
{

    /// <summary>
    /// this is 
    /// </summary>
    public class PlexLibrary
    {
        public string? Key { get; set; }
        public string? Title { get; set; }
        public string? Type { get; set; }
        public override string ToString()
        {
            return $"{Title} ({Type}) [Key={Key}]";
        }
    }

    public class PlexPlay{
        public string MediaTitle = "";
        public string VideoType= "";
        public long ViewedAt = -1;
        public string AccountID = "";
        public string DeviceID = "";
    };

    class Plex
    {
        // public static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");

        public string baseUrl = "";

        public string user = "";
        public string pwd = "";

        public string token = "";

        public HttpClient http = new HttpClient();
        // public XDocument? doc; //XDocument.Parse(xml);
        public List<PlexLibrary> plexLibraries { get; private set; } = new();

        //this will store the movies and shows, with the key being the fill path (aka ContentPath)
        public Dictionary<string, object> items = new Dictionary<string, object>();

        public string basePath = Directory.GetCurrentDirectory();

        public string cacheName = "plex_cache.json";

        private string cacheFile = "";

        public bool isLoaded = false;
        public double ageInDays = 0.0;

        public Plex(string iBaseUrl, string iuser,string ipassword,bool loadCacheFile = false)
        {
            baseUrl = iBaseUrl;
            user = iuser;
            pwd = ipassword;

            

            cacheFile = Path.Combine(basePath, cacheName);
            if (loadCacheFile)
            {
                loadCache();
            }
        }

        public Plex(bool loadCacheFile = false)
        {
            cacheFile = Path.Combine(basePath, cacheName);

            if (loadCacheFile)
            {
                loadCache();
            }
        }

        public void loadCache()
        {
            // Stopwatch sw = new Stopwatch();
            // sw.Start();

            if (File.Exists(cacheFile))
            {
                FileInfo fi = new FileInfo(cacheFile);

                ageInDays = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalDays;

                //if the cache is less than 1 day old
                if (ageInDays < 1.0)
                {
                    string content = File.ReadAllText(Path.Combine(basePath, cacheName));
                    items = Json5.Deserialize<Dictionary<string, object>>(content) ?? new Dictionary<string, object>();

                    isLoaded = true;
                    // logger.Info($"plex cache loaded");
                }
            }

            // sw.Stop();
            // logger.Info($"Loaded from cache in {sw.ElapsedMilliseconds}milsec or {sw.Elapsed}sec");
        }




        public async Task GetTokenAsync()
        {
            // Basic Auth to plex.tv/users/sign_in.json
            var req = new HttpRequestMessage(HttpMethod.Post, "https://plex.tv/users/sign_in.json");
            var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{pwd}"));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);

            // Required X-Plex headers
            req.Headers.Add("X-Plex-Product", "MyApp");
            req.Headers.Add("X-Plex-Version", "1.0");
            req.Headers.Add("X-Plex-Client-Identifier", "your-static-guid-here");
            req.Headers.Add("X-Plex-Platform", "Windows");
            req.Headers.Add("X-Plex-Device", "PC");

            // Body can be empty; using form type keeps some proxies happy
            req.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());

            var res = await http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            token = doc.RootElement.GetProperty("user").GetProperty("authToken").GetString() ?? "";

        }

        public double GetNormalizedScore(string name, List<(string Title, double Score)> list)
        {
            var match = list.FirstOrDefault(x => x.Title.Equals(name, StringComparison.OrdinalIgnoreCase));
            return match == default ? 0.0 : match.Score;
        }

        private async Task<List<PlexPlay>> get_PlexPlays(string period = "year")
        {
            try
            {
                var sectionsXml = await http.GetStringAsync($"{baseUrl}/library/sections?X-Plex-Token={token}");
                plexLibraries = XDocument.Parse(sectionsXml).Descendants("Directory")
                        .Where(n =>
                        {
                            var t = (string?)n.Attribute("type");
                            return t == "movie" || t == "show";
                        })
                        .Select(d => new PlexLibrary
                        {
                            Key = (string?)d.Attribute("key"),
                            Title = (string?)d.Attribute("title"),
                            Type = (string?)d.Attribute("type")
                        })
                        .ToList();

                //this gets the plays for the last year (for everyone)
                var selectionsXml = await http.GetStringAsync(
                    $"{baseUrl}/status/sessions/history/all?activeTimePeriod={period}&X-Plex-Token={token}&sort=viewedAt%3Adesc");
                var result = XDocument.Parse(selectionsXml).Descendants("Video") //.ToList();
                    .Select(d => new PlexPlay
                    {
                        // Use grandparentTitle for episodes (show name), otherwise title for movies
                        MediaTitle = d.Attribute("type")?.Value == "episode"
                            ? (string?)d.Attribute("grandparentTitle") ?? "Unknown Show"
                            : (string?)d.Attribute("title") ?? "Unknown Movie",

                        // Unix timestamp when the play happened
                        ViewedAt = (long.TryParse(d.Attribute("viewedAt")?.Value, out var ts) ? ts : 0L),
                        // ViewedAt = 1.0,

                        // Media type
                        VideoType = (string?)d.Attribute("type") ?? "unknown",

                        // Optional IDs if you need them later
                        AccountID = (string?)d.Attribute("accountID") ?? "unknown",
                        DeviceID  = (string?)d.Attribute("deviceID") ?? "unknown"
                    })
                    .Where(x => x.ViewedAt > 0) // discard malformed plays
                    .ToList();

                return result;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "issue getting token");
                return new List<PlexPlay>();
            }

        }


        public record NormalizedPlexPlays(
            List<(string Title, double Score)> Movies,
            List<(string Title, double Score)> Shows
        );
        /// <summary>
        /// provides a normalized list of plex plays, using a weighted score system
        /// </summary>
        /// <returns></returns>
        private async Task<NormalizedPlexPlays> get_NormalizedPlexPlays()
        {
            var plexPlaysMovies = null as List<(string Title, double Score)>;
            var plexPlaysShows  = null as List<(string Title, double Score)>;
            var plexPlays = null as List<PlexPlay>;
            var movies = null as List<PlexPlay>;
            var shows  = null as List<PlexPlay>;
            var movieCounts = new Dictionary<string,float>();
            var showCounts = new Dictionary<string,float>();


            //periods with weights
            var periods = new List<(string Name, float Value)>
            {
                ("all", 0.1f),
                ("year", 0.5f),
                ("month", 0.75f),
                ("week", 1.0f)
            };

            foreach (var p in periods)
            {
                plexPlays = await get_PlexPlays(p.Name);

                // Split into movies vs shows
                movies = plexPlays.Where(x => x.VideoType == "movie").ToList();
                shows  = plexPlays.Where(x => x.VideoType == "episode").ToList();

                foreach (var m in movies)
                {
                    if (movieCounts.ContainsKey(m.MediaTitle))
                    {
                        movieCounts[m.MediaTitle] += p.Value;
                    }
                    else
                    {
                        movieCounts[m.MediaTitle] = p.Value;;
                    }
                }

                foreach (var s in shows)
                {
                    if (showCounts.ContainsKey(s.MediaTitle))
                    {
                        showCounts[s.MediaTitle] += p.Value;
                    }
                    else
                    {
                        showCounts[s.MediaTitle] = p.Value;
                    }
                }


            }

            // Create your final 0.0 → 1.0 scored lists
            plexPlaysMovies = Misc.NormalizeQuantile(movieCounts);
            plexPlaysShows  = Misc.NormalizeQuantile(showCounts);

            /*
            File.WriteAllText(
                "plex_plays_movies.json",
                Json5.Serialize(plexPlaysMovies)
            );
            */

            return new NormalizedPlexPlays(
                Movies: plexPlaysMovies,
                Shows: plexPlaysShows
            );

        }


        public async Task LoadAsync(bool forceReload = false)
        {
            ////debugging
            // logger.Info($"isLoaded: {isLoaded} forceReload: {forceReload}");

            //data was already loaded from the cache file... use forceReload true
            if (isLoaded && forceReload == false)
            {
                return;
            }

            try
            {
                await GetTokenAsync();
            }
            catch (Exception ex)
            {
                logger.Error(ex,"issue getting token");
            }

            if (token == "")
            {
                isLoaded = false;
                return;
            }

            // //debugging
            // Stopwatch sw = new Stopwatch();
            // sw.Start();

            try
            {




                var NPPs = await get_NormalizedPlexPlays();

                // Create your final 0.0 → 1.0 scored lists
                var plexPlaysMovies = NPPs.Movies;
                var plexPlaysShows  = NPPs.Shows;

                foreach (var pl in plexLibraries)
                {
                    var itemsXml = await http.GetStringAsync(
                    $"{baseUrl}/library/sections/{pl.Key}/all?X-Plex-Token={token}");
                    var itemsDoc = XDocument.Parse(itemsXml);
                    var i = itemsDoc.Descendants()
                        .Select(v => new
                        {
                            title = (string?)v.Attribute("title"),
                            name = (string?)v.Attribute("title"),
                            contentRating = (string?)v.Attribute("contentRating"),
                            contentRatingAge = (string?)v.Attribute("contentRatingAge"),
                            summary = (string?)v.Attribute("summary"),
                            rating = (string?)v.Attribute("rating"),
                            audienceRating = (string?)v.Attribute("audienceRating"),
                            userRating = (string?)v.Attribute("userRating"),
                            viewCount = (string?)v.Attribute("viewCount"),
                            NView = pl.Type == "movie"
                                ? GetNormalizedScore((string?)v.Attribute("title") ?? "", plexPlaysMovies)
                                : GetNormalizedScore((string?)v.Attribute("title") ?? "", plexPlaysShows),
                            lastViewedAt = (string?)v.Attribute("lastViewedAt"),
                            lastRatedAt = (string?)v.Attribute("lastRatedAt"),
                            year = (string?)v.Attribute("year"),
                            duration = (string?)v.Attribute("duration"),
                            tags = string.Join(";", v.Descendants("Genre")
                                .Select(
                                    t => (string?)t.Attribute("tag")
                                )
                                .ToList()),
                            tagList = v.Descendants("Genre")
                                .Select(
                                    t => (string?)t.Attribute("tag")
                                )
                                .ToList(),
                            files = string.Join(";",
                                v.Descendants("Part")
                                .Select(p => (string?)p.Attribute("file"))
                                .Where(f => !string.IsNullOrEmpty(f))),
                            fileList = v.Descendants("Part")
                                .Select(p => (string?)p.Attribute("file"))
                                .Where(f => !string.IsNullOrEmpty(f)).ToList(),
                            ratingKey = (string?)v.Attribute("ratingKey"),
                            type = (string?)v.Attribute("type")
                        });

                    foreach (var x in i)
                    {
                        if (x.type == "movie")
                        {
                            foreach (var f in x.fileList)
                            {
                                Dictionary<string, object> dict = new Dictionary<string, object>();
                                dict["plex_title"] = x.title ?? "null";
                                dict["plex_name"] = x.name ?? "null";
                                dict["plex_contentRating"] = x.contentRating ?? "0";
                                dict["plex_contentRatingAge"] = x.contentRatingAge ?? "0";
                                dict["plex_summary"] = x.summary ?? "null";
                                dict["plex_rating"] = x.rating ?? "0";
                                dict["plex_audienceRating"] = x.audienceRating ?? "0";
                                dict["plex_userRating"] = x.userRating ?? "0";
                                dict["plex_viewCount"] = x.viewCount ?? "0";
                                dict["plex_nview"] = x.NView;
                                dict["plex_lastViewedAt"] = x.lastViewedAt ?? "0";
                                dict["plex_lastRatedAt"] = x.lastRatedAt ?? "0";
                                dict["plex_year"] = x.year ?? "0";
                                dict["plex_duration"] = x.duration ?? "0";
                                dict["plex_tags"] = x.tags ?? "";
                                dict["plex_tagList"] = x.tagList;
                                dict["plex_files"] = x.files ?? "";
                                dict["plex_fileList"] = x.fileList;
                                dict["plex_ratingKey"] = x.ratingKey ?? "-1";
                                dict["plex_type"] = x.type ?? "null";

                                if (f != null)
                                {
                                    items[f.ToString()] = dict;
                                }
                            }
                        }

                        else if (x.type == "show")
                        {

                            var epsXml = await http.GetStringAsync(
                                $"{baseUrl}/library/metadata/{x.ratingKey}/allLeaves?X-Plex-Token={token}");
                            var epsDoc = XDocument.Parse(epsXml);
                            var eps = epsDoc
                                .Descendants("Part")
                                .Select(
                                    v => new
                                    {
                                        file = (string?)v.Attribute("file")
                                    }
                                    );

                            foreach (var e in eps)
                            {
                                if (e.file == null)
                                {
                                    continue;
                                }

                                Dictionary<string, object> dict = new Dictionary<string, object>();
                                dict["plex_title"] = x.title ?? "null";
                                dict["plex_name"] = x.name ?? "null";
                                dict["plex_contentRating"] = x.contentRating ?? "0";
                                dict["plex_contentRatingAge"] = x.contentRatingAge ?? "0";
                                dict["plex_summary"] = x.summary ?? "null";
                                dict["plex_rating"] = x.rating ?? "0";
                                dict["plex_audienceRating"] = x.audienceRating ?? "0";
                                dict["plex_userRating"] = x.userRating ?? "0";
                                dict["plex_viewCount"] = x.viewCount ?? "0";
                                dict["plex_nview"] = x.NView;
                                dict["plex_lastViewedAt"] = x.lastViewedAt ?? "0";
                                dict["plex_lastRatedAt"] = x.lastRatedAt ?? "0";
                                dict["plex_year"] = x.year ?? "0";
                                dict["plex_duration"] = x.duration ?? "0";
                                dict["plex_tags"] = x.tags ?? "";
                                dict["plex_tagList"] = x.tagList;
                                dict["plex_files"] = e.file?.ToString() ?? "";
                                dict["plex_fileList"] = e.file?.ToString() ?? "";
                                dict["plex_ratingKey"] = x.ratingKey ?? "-1";
                                dict["plex_type"] = x.type ?? "null";

                                if (e.file != null)
                                {
                                    items[e.file.ToString()] = dict;
                                }

                            }
                        }
                    }
                }

                //saving the data for caching perposes
                ageInDays = 0.0;
                File.WriteAllText(
                    cacheName,
                    Json5.Serialize(items)
                );

                isLoaded = true;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                isLoaded = false;
            }

            // //debugging
            // sw.Stop();
            // logger.Info($"Loaded in {sw.ElapsedMilliseconds}milsec or {sw.Elapsed}sec");
        }

        /// <summary>
        /// allows to search the plex data
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public Dictionary<string, object> getDataByContentPath(string path)
        {
            if (items.TryGetValue(path, out var value))
            {
                return value as Dictionary<string, object> ?? new Dictionary<string, object>();
            }

            foreach (var entry in items)
            {
                if (entry.Key.StartsWith(path))
                {
                    return entry.Value as Dictionary<string, object> ?? new Dictionary<string, object>();
                }
            }
            return new Dictionary<string, object>();
        }

        /// <summary>
        /// a rename of getDataByContentPath
        /// </summary>
        /// <param name="contentPath"></param>
        /// <returns></returns>
        public Dictionary<string, object> getData(string contentPath)
        {
            return getDataByContentPath(contentPath);
        }


    }

}
