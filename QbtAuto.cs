/*
 * ============================================================================
 *  Project:      qbt_auto
 *  File:         QbtAuto.cs
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
using System.Reflection.PortableExecutable;
using System.Linq;   
using System.IO;     

namespace QbtAuto
{
    class QbtAuto
    {

        #region Inputs
        private  string? URL;
        private  string? USER;
        private  string? Password;
        public  string? ConfigPath;
        public  bool verbose = false;
        #endregion

        // Auto Itesm from the Config(s)
        public IEnumerable<object> autoTags = new List<object>();
        public IEnumerable<object> autoCategories = new List<object>();
        public IEnumerable<object> autoScripts = new List<object>();
        public IEnumerable<object> autoMoves = new List<object>();
        public IEnumerable<object> autoSpeeds = new List<object>();

        //interpreter
        public Interpreter it = new Interpreter();

        //Clients 
        public QBittorrentClient? qbt;
        public Plex plex = new Plex();

        //variables to store DriveData and Torrent Data
        public Dictionary<string, object> driveData = new Dictionary<string, object>();
        public IReadOnlyList<TorrentInfo>? torrents;

        //logger 
        public static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        public QbtAuto(string[] args)
        {
            Title();

            //processing Args
            var AP = new ArgParser(args);
            if (AP.has(["help", "?", "h"]))
            {
                logger.Info("qbt_auto | This is a qBittorrent automation tool");
                logger.Info("https://github.com/jgarza9788/qbt_auto/blob/master/README.md");
                return;
            }

            URL = AP.get(["host","H", "url"]);
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

            //if these values are on in the args, they can be in the config
            Dictionary<string, object>? qbt_login_data = getData(config.data, ["qbt", "qbtc", "qbt_connection"]) as Dictionary<string, object>;
            if (qbt_login_data != null)
            {
                URL ??= getData(qbt_login_data, ["host", "h", "url"]) as string;
                USER ??= getData(qbt_login_data, ["user", "u"]) as string;
                Password ??= getData(qbt_login_data, ["password", "p", "pwd"]) as string;
            }

            //see if plex is avliable
            plex = new Plex(loadCacheFile:true);
            Dictionary<string, object>? plex_login_data = getData(config.data, ["plex","Plex"]) as Dictionary<string, object>;
            if (plex_login_data != null)
            {
                plex.baseUrl = getData(plex_login_data, ["host", "url"]) as string ?? "";
                plex.token = getData(plex_login_data, ["token"]) as string ?? "";

                try
                {
                    plex.LoadAsync(forceReload: true).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger.Error(ex,"unable to get data from plex, see error");
                }
            }

            //maybe we'll just ask the user
            EnsureParametersValid();

            //print data
            logger.Info($"URL: {URL}");
            logger.Info($"USER: {USER}");
            logger.Info($"Password: ******");
            logger.Info($"ConfigPath: {ConfigPath}");
            logger.Info($"Plex.isLoaded: {plex.isLoaded}");
            logger.Info($"verbose: {verbose}");

            // Autos
            autoTags = config.getValue("autoTags") as IEnumerable<object> ?? new List<object>();
            autoCategories = config.getValue("autoCategories") as IEnumerable<object> ?? new List<object>();
            autoScripts = config.getValue("autoScripts") as IEnumerable<object> ?? new List<object>();
            autoMoves = config.getValue("autoMoves") as IEnumerable<object> ?? new List<object>();
            autoSpeeds = config.getValue("autoSpeeds") as IEnumerable<object> ?? new List<object>();

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

            // gets all the torrent data
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
            /*
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

            var spdTasks = torrents.Select(torrent =>
            {
                Dictionary<string, object>? T = Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrent));
                if (T == null) return Task.CompletedTask;

                return process_autoSpeeds(T);
            });
            await Task.WhenAll(spdTasks);
            */

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

            
        }

        
        public void Title()
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
        
        }
        
        
        /// <summary>
        /// this is only used when verbose is true
        /// </summary>
        /// <param name="torrents"></param>
        /// <returns></returns>
        public void logKeys(IReadOnlyList<TorrentInfo> torrents)
        {
            logger.Info("**Keys from Qbittorrent**");
            try
            {
                Dictionary<string, object>? T = Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrents[0]));

                if (T != null)
                {
                    foreach (var entry in T)
                    {
                        logger.Info($"\tkey=<{entry.Key}>\ttype={entry.Value?.GetType().ToString()}\texample={entry.Value?.ToString()}");
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
                    logger.Info($"\tkey=<{entry.Key}>\ttype={entry.Value.GetType()}\texample={entry.Value}");
                }
            }
            catch (Exception Ex)
            {
                logger.Error(Ex);
            }

            //plex items 
            logger.Info("**Keys from Plex**");
            if (plex.isLoaded)
            { 
                var pi = plex.items.First();
                var plexdata = plex.getData(pi.Key);
                foreach (var pd in plexdata)
                {
                    logger.Info($"\tkey=<{pd.Key}>\ttype={pd.Value.GetType()}\texample={pd.Value}");
                }

            }

                    
        }    


        /// <summary>
        /// gets the data from a Dictionary
        /// </summary>
        /// <param name="dict"></param>
        /// <param name="def"></param>
        /// <returns>object?</returns>
        private object? getData(Dictionary<string, object> dict, string[] keys, object? def = null)
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

        #region processing_methods
        /// <summary>
        /// runs all the processes 
        /// </summary>
        /// <param name="T"></param>
        /// <returns></returns>
        public async Task RunAllAutosAsync()
        {
            var list = torrents ?? Enumerable.Empty<TorrentInfo>(); 

            var tasks = list
                .Where(t => t != null) 
                .Select(torrent =>
                {
                    var T = Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrent));
                    return T == null ? Task.CompletedTask : process_all(T);
                });

            await Task.WhenAll(tasks);

            logger.Info("Processing completed.");
        }

        public async Task process_all(Dictionary<string, object> T)
        {
            await process_autoTags(T);
            await process_autoCategories(T);
            await process_autoScripts(T);
            await process_autoMoves(T);
            await process_autoSpeeds(T);
        }

        /// <summary>
        /// Processes automatic tagging for a given torrent based on predefined criteria.
        /// </summary>
        /// <param name="T"></param>
        /// <returns></returns>
        public async Task process_autoTags(Dictionary<string, object> T)
        {

            foreach (var autoTag in autoTags)
            {
                string tag = "";
                var AddTagCount = 0;
                var DeleteTagCount = 0;

                // Assuming autoTag is a Dictionary<string, object>
                if (autoTag is IDictionary<string, object> tagDict && tagDict.ContainsKey("tag"))
                {
                    var plexdata = plex.getData(T["ContentPath"].ToString() ?? "");

                    tag = tagDict["tag"].ToString() ?? "";
                    string criteria = Misc.Replacer(tagDict["criteria"].ToString() ?? "", new[] { T, driveData, plexdata });

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

                logger.Info($"{tag} Added:{AddTagCount} Deleted:{DeleteTagCount}");
            }
        }

        /// <summary>
        /// Processes automatic Categories for a given torrent based on predefined criteria.
        /// </summary>
        /// <param name="T"></param>
        /// <returns></returns>
        public async Task process_autoCategories(Dictionary<string, object> T)
        {

            foreach (var autoCategory in autoCategories)
            {
                // Assuming autoCategory is a Dictionary<string, object>
                if (autoCategory is IDictionary<string, object> catDict && catDict.ContainsKey("category"))
                {
                    var plexdata = plex.getData(T["ContentPath"].ToString() ?? "");

                    string category = catDict["category"].ToString() ?? "";
                    string criteria = Misc.Replacer(catDict["criteria"].ToString() ?? "", new[] { T, driveData, plexdata } );

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
        public async Task process_autoScripts(Dictionary<string, object> T)
        {

            foreach (var autoScript in autoScripts)
            {
                if (autoScript is IDictionary<string, object> scrDict && scrDict.ContainsKey("script"))
                {
                    var plexdata = plex.getData(T["ContentPath"].ToString() ?? ""); 

                    string name = scrDict["name"].ToString() ?? "";
                    string criteria = Misc.Replacer(scrDict["criteria"].ToString() ?? "", new[] { T, driveData, plexdata });

                    string directory = Misc.Replacer(scrDict["directory"].ToString() ?? "", new[] { T, driveData, plexdata });
                    char sep = directory.Contains('\\') ? '\\' : '/';
                    directory = directory + sep;

                    string shebang = Misc.Replacer(scrDict["shebang"].ToString() ?? "",new[] { T, driveData, plexdata });
                    if (shebang == "" && OperatingSystem.IsWindows())
                    {
                        shebang = "cmd.exe";
                    }
                    else if (shebang == "" && !OperatingSystem.IsWindows())
                    { 
                        shebang = "/bin/bang";
                    }

                    string script = Misc.Replacer(scrDict["script"].ToString() ?? "", new[] { T, driveData, plexdata });
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

        /// <summary>
        /// Processes automatic moves for a given torrent based on predefined criteria.
        /// </summary>
        /// <param name="T"></param>
        /// <returns></returns>
        public async Task process_autoMoves(Dictionary<string, object> T)
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

                    var plexdata = plex.getData(T["ContentPath"].ToString() ?? ""); 

                    string path = Misc.Replacer(movDict["path"].ToString() ?? "", new[] { T, driveData, plexdata });
                    string criteria = Misc.Replacer(movDict["criteria"].ToString() ?? "", new[] { T, driveData, plexdata });

                    char sep = path.Contains('\\') ? '\\' : '/';

                    // todo
                    string logString = !verbose ? $"{T["Name"]}" : $"Name:{T["Name"]}\npath:{path}\ncriteria{criteria}";

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

                    if (!Directory.Exists(path))
                    {
                        logger.Warn($"path does not exists,\n{logString}");
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
        /// Processes automatic changes the speed(s) for a given torrent based on predefined criteria.
        /// </summary>
        /// <param name="T"></param>
        /// <returns></returns>
        public async Task process_autoSpeeds(Dictionary<string, object> T)
        {
            foreach (var autoSpeed in autoSpeeds)
            {
                if (autoSpeed is IDictionary<string, object> spdDict)
                {

                    var plexdata = plex.getData(T["ContentPath"].ToString() ?? ""); 

                    long uploadSpeed = Int64.Parse(spdDict["uploadSpeed"].ToString() ?? "-1") * 1024;
                    long downloadSpeed = Int64.Parse(spdDict["downloadSpeed"].ToString() ?? "-1") * 1024;
                    string criteria = Misc.Replacer(spdDict["criteria"].ToString() ?? "", new[] { T, driveData, plexdata });

                    string logString = !verbose ? $"{T["Name"]}" : $"Name:{T["Name"]}\nuploadSpeed:{uploadSpeed}\ndownloadSpeed:{downloadSpeed}\ncriteria{criteria}";

                    bool shouldChange = false;
                    try
                    {
                        shouldChange = it.Eval<bool>(criteria);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, logString);
                        continue;
                    }

                    try
                    {
                        if (shouldChange)
                        {
                            if (uploadSpeed >= 0)
                            {
                                await qbt.SetTorrentUploadLimitAsync(T["Hash"].ToString(), uploadSpeed);
                                logger.Info($"Set uploadSpeed :: {T["Name"]} => {uploadSpeed} | {logString}");
                            }
                            if (downloadSpeed >= 0)
                            {
                                await qbt.SetTorrentDownloadLimitAsync(T["Hash"].ToString(), downloadSpeed);
                                logger.Info($"Set downloadSpeed :: {T["Name"]} => {downloadSpeed} | {logString}");
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

        #endregion

        /// <summary>
        /// EnsureParametersValid
        /// </summary>
        /// <returns></returns>
        private void EnsureParametersValid()
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
