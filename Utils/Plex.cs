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

    class Plex
    {
        public static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        public string baseUrl = "";
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

        public Plex(string iBaseUrl, string iToken,bool loadCacheFile = false)
        {
            baseUrl = iBaseUrl;
            token = iToken;

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


        public async Task LoadAsync(bool forceReload = false)
        {
            ////debugging
            //logger.Info($"isLoaded: {isLoaded} forceReload: {forceReload}");

            //data was already loaded from the cache file... use forceReload true
            if (isLoaded && forceReload == false)
            {
                return;
            }

            // //debugging
            // Stopwatch sw = new Stopwatch();
            // sw.Start();

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
                //Debugging
                /*
                foreach (var pl in plexLibraries)
                {
                    logger.Info($"{pl.Key} {pl.Title} {pl.Type}");
                }
                logger.Info(plexLibraries.Count());
                */

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
                                dict["plex_title"] = x.title ?? "";
                                dict["plex_name"] = x.name ?? "";
                                dict["plex_contentRating"] = x.contentRating ?? "";
                                dict["plex_contentRatingAge"] = x.contentRatingAge ?? "";
                                dict["plex_summary"] = x.summary ?? "";
                                dict["plex_rating"] = x.rating ?? "";
                                dict["plex_audienceRating"] = x.audienceRating ?? "";
                                dict["plex_userRating"] = x.userRating ?? "";
                                dict["plex_viewCount"] = x.viewCount ?? "";
                                dict["plex_lastViewedAt"] = x.lastViewedAt ?? "";
                                dict["plex_lastRatedAt"] = x.lastRatedAt ?? "";
                                dict["plex_year"] = x.year ?? "";
                                dict["plex_duration"] = x.duration ?? "";
                                dict["plex_tags"] = x.tags ?? "";
                                dict["plex_tagList"] = x.tagList;
                                dict["plex_files"] = x.files ?? "";
                                dict["plex_fileList"] = x.fileList;
                                dict["plex_ratingKey"] = x.ratingKey ?? "";
                                dict["plex_type"] = x.type ?? "";

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
                                dict["plex_title"] = x.title ?? "";
                                dict["plex_name"] = x.name ?? "";
                                dict["plex_contentRating"] = x.contentRating ?? "";
                                dict["plex_contentRatingAge"] = x.contentRatingAge ?? "";
                                dict["plex_summary"] = x.summary ?? "";
                                dict["plex_rating"] = x.rating ?? "";
                                dict["plex_audienceRating"] = x.audienceRating ?? "";
                                dict["plex_userRating"] = x.userRating ?? "";
                                dict["plex_viewCount"] = x.viewCount ?? "";
                                dict["plex_lastViewedAt"] = x.lastViewedAt ?? "";
                                dict["plex_lastRatedAt"] = x.lastRatedAt ?? "";
                                dict["plex_year"] = x.year ?? "";
                                dict["plex_duration"] = x.duration ?? "";
                                dict["plex_tags"] = x.tags ?? "";
                                dict["plex_tagList"] = x.tagList;
                                dict["plex_files"] = e.file?.ToString() ?? "";
                                dict["plex_fileList"] = e.file?.ToString() ?? "";
                                dict["plex_ratingKey"] = x.ratingKey ?? "";
                                dict["plex_type"] = x.type ?? "";

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
