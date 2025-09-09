/*
 * ============================================================================
 *  Project:      qbt_auto
 *  File:         Program.cs
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


using System;
using System.Collections;

using System.Net;
using System.Text.RegularExpressions;
using DynamicExpresso;
using Utils;
using QBittorrent.Client;
using Json5Core;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NLog.LayoutRenderers;
using Microsoft.VisualBasic;
using NLog;

namespace QbtAuto
{
    class Program
    {
        #region Inputs
        private static string? URL;
        private static string? USER;
        private static string? Password;
        private static string? ConfigPath;

        private static bool verbose = false;
        #endregion

        private static IEnumerable<object> autoTags = new List<object>();
        private static IEnumerable<object> autoCategories = new List<object>();
        private static IEnumerable<object> autoScripts = new List<object>();
        private static IEnumerable<object> autoMoves = new List<object>();

        private static Interpreter it = new Interpreter();

        private static QBittorrentClient? qbt;

        private static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        private static Dictionary<string, object> driveData = new Dictionary<string, object>();

        private static IReadOnlyList<TorrentInfo>? torrents;


        static async Task Main(string[] args)
        {

            Console.Write(@"
          $$\        $$\                                $$\               
          $$ |       $$ |                               $$ |              
 $$$$$$\  $$$$$$$\ $$$$$$\         $$$$$$\  $$\   $$\ $$$$$$\    $$$$$$\  
$$  __$$\ $$  __$$\\_$$  _|        \____$$\ $$ |  $$ |\_$$  _|  $$  __$$\ 
$$ /  $$ |$$ |  $$ | $$ |          $$$$$$$ |$$ |  $$ |  $$ |    $$ /  $$ |
$$ |  $$ |$$ |  $$ | $$ |$$\      $$  __$$ |$$ |  $$ |  $$ |$$\ $$ |  $$ |
\$$$$$$$ |$$$$$$$  | \$$$$  |     \$$$$$$$ |\$$$$$$  |  \$$$$  |\$$$$$$  |
 \____$$ |\_______/   \____/$$$$$$\\_______| \______/    \____/  \______/ 
      $$ |                  \______|                                      
      $$ |                                                                
      \__|


");


            try
            {

                var AP = new ArgParser(args);

                URL = AP.get(["host", "h", "url"]);
                USER = AP.get(["user", "u"]);
                Password = AP.get(["password", "p", "pwd"]);
                ConfigPath = AP.get(["config", "c", "configpath"]);

                string? v = AP.get(["v", "verbose"], "") ?? "";
                if (
                    v.Contains('1', StringComparison.OrdinalIgnoreCase)
                    || v.Contains("TRUE", StringComparison.OrdinalIgnoreCase)
                    || v.Contains("YES", StringComparison.OrdinalIgnoreCase)
                    || v.Contains("ON", StringComparison.OrdinalIgnoreCase)
                    || v.Contains("T", StringComparison.OrdinalIgnoreCase)
                    )
                {
                    verbose = true;
                }

                ConfigPath = string.IsNullOrWhiteSpace(ConfigPath) ? "" : ConfigPath;
                DataObject config = new DataObject(ConfigPath);
                logger.Info("Config loaded.");

                // if (verbose)
                // {
                //     logger.Info(config.ToString());
                // }

                //if these values are on in the args, they can be in the config
                    Dictionary<string, object>? qbt_login_data = getData(config.data, ["qbt", "qbtc", "qbt_connection"]) as Dictionary<string, object>;
                if (qbt_login_data != null)
                {
                    URL ??= getData(qbt_login_data, ["host", "h", "url"]) as string;
                    USER ??= getData(qbt_login_data, ["user", "u"]) as string;
                    Password ??= getData(qbt_login_data, ["password", "p", "pwd"]) as string;
                }

                //maybe we'll just ask the user
                EnsureParametersValid();

                //print data
                logger.Info($"URL: {URL}");
                logger.Info($"USER: {USER}");
                logger.Info($"Password: ******");
                logger.Info($"ConfigPath: {ConfigPath}");
                logger.Info($"verbose: {verbose}");

                autoTags = config.getValue("autoTags") as IEnumerable<object> ?? new List<object>();
                autoCategories = config.getValue("autoCategories") as IEnumerable<object> ?? new List<object>();
                autoScripts = config.getValue("autoScripts") as IEnumerable<object> ?? new List<object>();
                autoMoves = config.getValue("autoMoves") as IEnumerable<object> ?? new List<object>();

                it = new Interpreter()
                    .SetFunction("contains", (string t, string s) => t.Contains(s))
                    .SetFunction("match", (string t, string p) => Regex.IsMatch(t, p))
                    .SetFunction("daysAgo", (string iso) => (DateTime.UtcNow - DateTime.Parse(iso)).TotalDays)
                    .SetDefaultNumberType(DefaultNumberType.Double);


                var httpHandler = new SocketsHttpHandler
                {
                    MaxConnectionsPerServer = 100,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    AutomaticDecompression = DecompressionMethods.All
                };
                // var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

                qbt = new QBittorrentClient(new Uri(URL!), httpHandler, disposeHandler: false);
                qbt.LoginAsync(USER!, Password!).GetAwaiter().GetResult();
                logger.Info($"Connected to qBittorrent.");

                torrents = qbt.GetTorrentListAsync().GetAwaiter().GetResult();

                //getting the driveadata
                driveData = Drives.getDriveData();

                //used for debugging
                /*
                qbt.AddTorrentTagAsync("c1545b527137317de5a3667031d7b922c873d484", "test").GetAwaiter().GetResult();
                */

                #region verbose_for_testing
                if (verbose)
                {
                    logKeys(torrents);
                }
                #endregion

                // Run process_autoTags for all torrents in parallel
                
                var atTasks = torrents.Select(torrent =>
                {
                    Dictionary<string, object>? T = Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrent));
                    if (T == null) return Task.CompletedTask;

                    return process_autoTags(T);
                });
                await Task.WhenAll(atTasks);

                var acTasks = torrents.Select(torrent =>
                {
                    Dictionary<string, object>? T = Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrent));
                    if (T == null) return Task.CompletedTask;

                    return process_autoCategories(T);
                });
                await Task.WhenAll(acTasks);

                var asTasks = torrents.Select(torrent =>
                {
                    Dictionary<string, object>? T = Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrent));
                    if (T == null) return Task.CompletedTask;

                    return process_autoScripts(T);
                });
                await Task.WhenAll(asTasks);
                
                var amTasks = torrents.Select(torrent =>
                {
                    Dictionary<string, object>? T = Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrent));
                    if (T == null) return Task.CompletedTask;

                    return process_autoMoves(T);
                });
                await Task.WhenAll(amTasks);

                //this will run all at once, however the log looks like 💩
                /*
                var task = torrents.Select(torrent =>
                {
                    Dictionary<string, object>? T = Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrent));
                    if (T == null) return Task.CompletedTask;

                    return process_all(T);
                });
                await Task.WhenAll(task);
                */

                logger.Info("Processing completed.");

            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }

        }

        /// <summary>
        /// this is only used when verbose is true
        /// </summary>
        /// <param name="torrents"></param>
        /// <returns></returns>
        public static void logKeys(IReadOnlyList<TorrentInfo> torrents)
        {
            logger.Info("**Keys from Qbittorrent**");
            try
            {
                Dictionary<string, object>? T = Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrents[0]));

                if (T != null)
                {
                    foreach (var entry in T)
                    {
                        logger.Info($"key=<{entry.Key}>\ttype={entry.Value?.GetType().ToString()}\texample={entry.Value?.ToString()}");
                    }
                }

            }
            catch (Exception Ex)
            {
                logger.Error(Ex);
            }

            logger.Info("**Keys from Drives**");
            try
            {
                foreach (var entry in driveData)
                {
                    logger.Info($"key=<{entry.Key}>\ttype={entry.Value.GetType()}\texample={entry.Value}");
                }
            }
            catch (Exception Ex)
            {
                logger.Error(Ex);
            }
        }    


        /// <summary>
        /// gets the data from a Dictionary
        /// </summary>
        /// <param name="dict"></param>
        /// <param name="def"></param>
        /// <returns>object?</returns>
        private static object? getData(Dictionary<string, object> dict, string[] keys, object? def = null)
        {
            foreach (string key in keys)
            {
                if (dict.TryGetValue(key, out object? value))
                {
                    return value;
                }
            }

            return def;
        }

        /// <summary>
        /// Replaces placeholders in the criteria string with actual values from the dictionary.
        /// </summary>
        /// <param name="Dict"></param>
        /// <param name="criteria"></param>
        /// <returns></returns>
        private static string makeCriteria(Dictionary<string, object> Dict, string criteria)
        {

            foreach (var entry in Dict)
            {
                try
                {
                    string value = "";

                    if (entry.Value is System.Collections.IList)
                    {
                        var enumerable = (entry.Value as System.Collections.IList)?.Cast<object>() ?? new List<object>();
                        value = string.Join(",", enumerable);
                    }
                    else
                    {
                        value = entry.Value?.ToString() ?? "";
                    }

                    criteria = criteria.Replace($"<{entry.Key}>", value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"**error** {entry.Key} {entry.Value?.ToString()} {criteria}");
                    logger.Error(ex);
                }

            }

            return criteria;
        }
        private static string makeCriteria(Dictionary<string, object>[] Dicts, string criteria)
        {
            foreach (var Dict in Dicts)
            {
                foreach (var entry in Dict)
                {
                    try
                    {
                        string value = "";

                        if (entry.Value is System.Collections.IList)
                        {
                            var enumerable = (entry.Value as System.Collections.IList)?.Cast<object>() ?? new List<object>();
                            value = string.Join(",", enumerable);
                        }
                        else
                        {
                            value = entry.Value?.ToString() ?? "";
                        }

                        criteria = criteria.Replace($"<{entry.Key}>", value);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"**error** {entry.Key} {entry.Value?.ToString()} {criteria}");
                        logger.Error(ex);
                    }

                }
            }
            return criteria;
        }

        /// <summary>
        /// runs all the processes 
        /// </summary>
        /// <param name="T"></param>
        /// <returns></returns>
        private static async Task process_all(Dictionary<string, object> T)
        {
            await process_autoTags(T);
            await process_autoCategories(T);
            await process_autoScripts(T);
        }

        /// <summary>
        /// Processes automatic tagging for a given torrent based on predefined criteria.
        /// </summary>
        /// <param name="T"></param>
        /// <returns></returns>
        private static async Task process_autoTags(Dictionary<string, object> T)
        {

            foreach (var autoTag in autoTags)
            {
                // Assuming autoTag is a Dictionary<string, object>
                if (autoTag is IDictionary<string, object> tagDict && tagDict.ContainsKey("tag"))
                {
                    string tag = tagDict["tag"].ToString() ?? "";
                    string criteria = makeCriteria(new[] { T, driveData }, tagDict["criteria"].ToString() ?? "");

                    string currentTags = T["Tags"] is IEnumerable<object> ctlist
                        ? string.Join(",", ctlist)
                        : T["Tags"]?.ToString() ?? "";

                    string hash = T["Hash"]?.ToString() ?? "";
                    string logString = !verbose ? $"{T["Name"]} {tag}" : $"Name:{T["Name"]}\nHash{hash}\nTag:{tag}\ncriteria{criteria}";

                    bool shouldHaveTag = false;
                    try
                    {
                        shouldHaveTag = it.Eval<bool>(criteria);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, logString);
                        continue;
                    }

                    try
                    {
                        if (shouldHaveTag)
                        {
                            if (!currentTags.Contains(tag))
                            {
                                await qbt.AddTorrentTagAsync(T["Hash"].ToString(), tag);
                                logger.Info($"AddTag :: {T["Name"]} + {tag}");
                            }

                        }
                        else
                        {
                            if (currentTags.Contains(tag))
                            {
                                await qbt.DeleteTorrentTagAsync(T["Hash"].ToString(), tag);
                                logger.Info($"DeleteTag :: {T["Name"]} - {tag}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, logString);
                        continue;
                    }
                }
            }
        }

        /// <summary>
        /// Processes automatic Categories for a given torrent based on predefined criteria.
        /// </summary>
        /// <param name="T"></param>
        /// <returns></returns>
        private static async Task process_autoCategories(Dictionary<string, object> T)
        {

            foreach (var autoCategory in autoCategories)
            {
                // Assuming autoCategory is a Dictionary<string, object>
                if (autoCategory is IDictionary<string, object> catDict && catDict.ContainsKey("category"))
                {
                    string category = catDict["category"].ToString() ?? "";
                    string criteria = makeCriteria(new[] { T, driveData }, catDict["criteria"].ToString() ?? "");

                    string currentCategory = T["Category"]?.ToString() ?? "";

                    double Progress = Convert.ToDouble(T["Progress"]);
                    string SavePath = T["SavePath"]?.ToString() ?? "";
                    char sep = SavePath.Contains('\\') ? '\\' : '/';

                    // Split into parent dir + last segment
                    string parentDir = SavePath.Contains(sep)
                        ? SavePath[..SavePath.LastIndexOf(sep)]
                        : ""; // root-level

                    // Construct the new location with the SAME separator style
                    string newLocation = string.IsNullOrEmpty(parentDir)
                        ? category
                        : $"{parentDir}{sep}{category}";

                    string logString = !verbose ? $"{T["Name"]} {category}" : $"Name:{T["Name"]}\nHash:{T["Hash"]?.ToString()}\ncategory:{category}\ncriteria{criteria}";

                    bool shouldHaveCategory = false;
                    try
                    {
                        shouldHaveCategory = it.Eval<bool>(criteria);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, logString);
                        continue;
                    }

                    string hash = T["Hash"]?.ToString() ?? "";

                    try
                    {
                        if (shouldHaveCategory)
                        {
                            if (currentCategory != category)
                            {
                                await qbt.SetTorrentCategoryAsync(T["Hash"].ToString(), category);
                                logger.Info($"SetCategory :: {T["Name"]} => {category}");


                                //if it's done downloading, we will move the location
                                if (Progress.Equals(1.0))
                                {
                                    await qbt.SetAutomaticTorrentManagementAsync(T["Hash"].ToString(), false);
                                    await qbt.SetLocationAsync(T["Hash"].ToString(), newLocation);

                                    logger.Info($"MovedTorrent :: {T["Name"]} => {newLocation}");
                                }
                            }

                        }
                        else
                        {
                            // No action needed if the category does not match
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, logString);
                        continue;
                    }
                }
            }
        }

        /// <summary>
        /// Processes automatic Scripts for a given torrent based on predefined criteria.
        /// </summary>
        /// <param name="T"></param>
        /// <returns></returns>
        private static async Task process_autoScripts(Dictionary<string, object> T)
        {

            foreach (var autoScript in autoScripts)
            {
                if (autoScript is IDictionary<string, object> scrDict && scrDict.ContainsKey("script"))
                {
                    
                    string name = scrDict["name"].ToString() ?? "";
                    string criteria = makeCriteria(new[] { T, driveData }, scrDict["criteria"].ToString() ?? "");

                    string directory = makeCriteria(new[] { T, driveData }, scrDict["directory"].ToString() ?? "");
                    char sep = directory.Contains('\\') ? '\\' : '/';
                    directory = directory + sep;

                    string shebang = makeCriteria(new[] { T, driveData }, scrDict["shebang"].ToString() ?? "");
                    if (shebang == "" && OperatingSystem.IsWindows())
                    {
                        shebang = "cmd.exe";
                    }
                    else if (shebang == "" && !OperatingSystem.IsWindows())
                    { 
                        shebang = "/bin/bang";
                    }

                    string script = makeCriteria(new[] { T, driveData }, scrDict["script"].ToString() ?? "");
                    long timeout = (long)scrDict["timeout"];

                    string logString = !verbose ? $"{T["Name"]} {name}" : $"\ntorrent{T["Name"]}\nname:{name}\ndirectory:{directory}\ncriteria:{criteria}\nshebang:{shebang}\nscript:{script}";

                    if (!Directory.Exists(directory))
                    {
                        logger.Warn($"Directory does not exists {logString}");
                        continue;
                    }

                    bool shouldRun = false;
                    try
                    {
                        shouldRun = it.Eval<bool>(criteria);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"{logString}");
                        continue;
                    }

                    string hash = T["Hash"]?.ToString() ?? "";

                    if (!shouldRun)
                    { 
                        logger.Info($"Did not match criteria {logString}");
                        continue;
                    }

                    try
                    {
                        if (shouldRun)
                        {
                            if (File.Exists($"{directory}{sep}{name}"))
                            {
                                logger.Warn($"The Script has been ran for this item.\ndelete \"{directory}{sep}{name}\" to allow a re-run of this script {logString}");
                            }
                            else
                            {

                                var r = await Utils.Cmd.SheBangCmdAsync(shebang, script, directory, (int)timeout);
                                logger.Info($"{logString}\n{r.ExitCode}|{r.StdOut}|{r.StdErr}\n{logString}");

                                await File.WriteAllTextAsync($"{directory}{sep}{name}", "");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, logString);
                        continue;
                    }
                }
            }
        }

        private static async Task process_autoMoves(Dictionary<string, object> T)
        {
            foreach (var autoMove in autoMoves)
            {
                if (autoMove is IDictionary<string, object> movDict)
                {
                    double Progress = Convert.ToDouble(T["Progress"]);
                    if (Progress < 1.0)
                    {
                        logger.Info($"{T["Name"]} {Progress} - not complete yet");
                        continue;
                    }

                    string path = makeCriteria(new[] { T, driveData }, movDict["path"].ToString() ?? "");
                    string criteria = makeCriteria(new[] { T, driveData }, movDict["criteria"].ToString() ?? "");

                    char sep = path.Contains('\\') ? '\\' : '/';

                    // todo
                    string logString = !verbose ? $"{T["Name"]}" : $"Name:{T["Name"]}\npath:{path}\ncriteria{criteria}";
                    logger.Info(logString);

                    try
                    {
                        Directory.CreateDirectory(path);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, logString);
                        continue;
                    }

                    bool shouldMove = false;
                    try
                    {
                        shouldMove = it.Eval<bool>(criteria);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, logString);
                        continue;
                    }

                    try
                    {
                        if (shouldMove)
                        {
                            await qbt.SetAutomaticTorrentManagementAsync(T["Hash"].ToString(), false);
                            await qbt.SetLocationAsync(T["Hash"].ToString(), path);

                            logger.Info($"MovedTorrent :: {T["Name"]} => {path} | {logString}");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, logString);
                        continue;
                    }



                }
            }
        }

        /// <summary>
        /// EnsureParametersValid
        /// </summary>
        /// <returns></returns>
        private static void EnsureParametersValid()
        {
            // Ask for URL if not provided
            while (string.IsNullOrWhiteSpace(URL))
            {
                Console.Write("Enter qBittorrent URL: ");
                URL = Console.ReadLine();
            }

            // Ask for USER if not provided
            while (string.IsNullOrWhiteSpace(USER))
            {
                Console.Write("Enter qBittorrent Username: ");
                USER = Console.ReadLine();
            }

            // Ask for Password if not provided
            while (string.IsNullOrWhiteSpace(Password))
            {
                Console.Write("Enter qBittorrent Password: ");
                Password = Console.ReadLine();
            }

            // Ask for ConfigPath if not provided or file not found
            while (string.IsNullOrWhiteSpace(ConfigPath) || !System.IO.File.Exists(ConfigPath))
            {
                Console.Write("Enter path to config file (must exist): ");
                ConfigPath = Console.ReadLine();
            }

        }
    }



}
