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


using System.Net;
using Utils;
using QBittorrent.Client;
using Json5Core;
using System.Diagnostics;

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

        // Auto Itesm from the Config(s) - obsolite
        /*
        public IEnumerable<object> autoTags = new List<object>();
        public IEnumerable<object> autoCategories = new List<object>();
        public IEnumerable<object> autoScripts = new List<object>();
        public IEnumerable<object> autoMoves = new List<object>();
        public IEnumerable<object> autoSpeeds = new List<object>();
        */

        List<AutoBase> Autos = new List<AutoBase>();
        List<Dictionary<string, object>> globalDicts = new List<Dictionary<string, object>>();

        //Clients 
        public QBittorrentClient? qbt;
        public Plex plex = new Plex();

        //variables to store DriveData and Torrent Data
        public Dictionary<string, object> driveData = new Dictionary<string, object>();
        public IReadOnlyList<TorrentInfo>? torrents;

        //logger 
        // public static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        private static NLog.Logger loggerFC = NLog.LogManager.GetLogger("LoggerFC");
        private static NLog.Logger loggerF = NLog.LogManager.GetLogger("LoggerF");
        private static NLog.Logger loggerC = NLog.LogManager.GetLogger("LoggerC");


        public QbtAuto(string[] args)
        {
            Title();

            //processing Args
            var AP = new ArgParser(args);
            if (AP.has(["help", "?", "h"]))
            {
                loggerC.Info("qbt_auto | This is a qBittorrent automation tool");
                loggerC.Info("https://github.com/jgarza9788/qbt_auto/blob/master/README.md");
                return;
            }

            URL = AP.get(["host", "H", "url"]);
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
            loggerFC.Info("Config loaded.");

            //if these values are on in the args, they can be in the config
            Dictionary<string, object>? qbt_login_data = getData(config.data, ["qbt", "qbtc", "qbt_connection"]) as Dictionary<string, object>;
            if (qbt_login_data != null)
            {
                URL ??= getData(qbt_login_data, ["host", "h", "url"]) as string;
                USER ??= getData(qbt_login_data, ["user", "u"]) as string;
                Password ??= getData(qbt_login_data, ["password", "p", "pwd"]) as string;
            }

            //see if plex is avliable
            plex = new Plex(loadCacheFile: true);
            Dictionary<string, object>? plex_login_data = getData(config.data, ["plex", "Plex"]) as Dictionary<string, object>;
            if (plex_login_data != null)
            {
                plex.baseUrl = getData(plex_login_data, ["host", "url"]) as string ?? "";
                plex.user = getData(plex_login_data, ["user","u"]) as string ?? "";
                plex.pwd = getData(plex_login_data, ["pwd", "password", "p"]) as string ?? "";

                try
                {
                    plex.LoadAsync(forceReload: true).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    loggerF.Error(ex, "unable to get data from plex, see error");
                    loggerC.Error("unable to get data from plex, see error");
                }
            }

            //maybe we'll just ask the user
            EnsureParametersValid();

            //print data
            loggerFC.Info($"");
            loggerFC.Info($"URL: {URL}");
            loggerFC.Info($"USER: {USER}");
            loggerFC.Info($"Password: ******");
            loggerFC.Info($"ConfigPath: {ConfigPath}");
            loggerFC.Info($"Plex.isLoaded: {plex.isLoaded}");
            loggerFC.Info($"verbose: {verbose}");
            loggerFC.Info($"");

            var httpHandler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 100,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                AutomaticDecompression = DecompressionMethods.All
            };
            qbt = new QBittorrentClient(new Uri(URL!), httpHandler, disposeHandler: false);
            qbt.LoginAsync(USER!, Password!).GetAwaiter().GetResult();
            loggerFC.Info($"Connected to qBittorrent.");

            //getting the driveadata
            driveData = Drives.getDriveData();
            globalDicts.Add(driveData);

            #region CreateAutoObjects

            // ** autoTags **
            IEnumerable<object> autoTags = config.getValue("autoTags") as IEnumerable<object> ?? new List<object>();
            foreach (IDictionary<string, object> autoTag in autoTags)
            {
                Autos.Add(new AutoTag(
                    autoTag["tag"].ToString() ?? "",
                    autoTag["criteria"].ToString() ?? "",
                    qbt,
                    plex,
                    globalDicts
                    ));
            } 

            // ** autoCategories **
            IEnumerable<object> autoCategories = config.getValue("autoCategories") as IEnumerable<object> ?? new List<object>();
            foreach (IDictionary<string, object> autoCat in autoCategories)
            {
                Autos.Add(new AutoCat(
                    autoCat["category"].ToString() ?? "",
                    autoCat["criteria"].ToString() ?? "",
                    qbt,
                    plex,
                    globalDicts
                    ));
            } 

            // ** autoScripts **
            IEnumerable<object> autoScripts = config.getValue("autoScripts") as IEnumerable<object> ?? new List<object>();
            foreach (IDictionary<string, object> autoScript in autoScripts)
            {
                Autos.Add(new AutoScript(
                    autoScript["name"].ToString() ?? "",
                    autoScript["directory"].ToString() ?? "",
                    autoScript["shebang"].ToString() ?? "",
                    autoScript["script"].ToString() ?? "",
                    (long)(autoScript["timeout"] ?? 100),
                    autoScript["criteria"].ToString() ?? "",
                    qbt,
                    plex,
                    globalDicts
                    ));
            } 

            // ** autoMoves **
            IEnumerable<object> autoMoves = config.getValue("autoMoves") as IEnumerable<object> ?? new List<object>();
            foreach (IDictionary<string, object> autoMov in autoMoves)
            {
                Autos.Add(new AutoMoves(
                    autoMov["path"].ToString() ?? "",
                    autoMov["criteria"].ToString() ?? "",
                    qbt,
                    plex,
                    globalDicts
                    ));
            } 

            // ** autoSpeeds **
            IEnumerable<object> autoSpeeds = config.getValue("autoSpeeds") as IEnumerable<object> ?? new List<object>();
            foreach (IDictionary<string, object> autoSpeed in autoSpeeds)
            {
                Autos.Add(new AutoSpeed(
                    (long)(autoSpeed["uploadSpeed"] ?? -1),
                    (long)(autoSpeed["downloadSpeed"] ?? -1),
                    autoSpeed["criteria"].ToString() ?? "",
                    qbt,
                    plex,
                    globalDicts
                    ));
            } 


            #endregion

            // gets all the torrent data
            torrents = qbt.GetTorrentListAsync().GetAwaiter().GetResult();



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
            loggerFC.Info(new string('#', 40));
            loggerFC.Info("**Keys from Qbittorrent**");
            try
            {
                Dictionary<string, object>? T = Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrents[0]));

                if (T != null)
                {
                    foreach (var entry in T)
                    {
                        loggerFC.Info($"\tkey=<{entry.Key}>\ttype={entry.Value?.GetType().ToString()}\texample={entry.Value?.ToString()}");
                    }
                }

            }
            catch (Exception Ex)
            {
                loggerF.Error(Ex);
                loggerC.Error("**ERROR** issue loading drive data");
            }

            loggerFC.Info(new string('#', 20));

            loggerFC.Info("**Keys from Drives**");
            try
            {
                foreach (var entry in driveData)
                {
                    loggerFC.Info($"\tkey=<{entry.Key}>\ttype={entry.Value.GetType()}\texample={entry.Value}");
                }
            }
            catch (Exception Ex)
            {
                loggerF.Error(Ex);
                loggerC.Error("**ERROR** issue loading drive data");
            }

            loggerFC.Info(new string('#', 20));

            //plex items 
            loggerFC.Info("**Keys from Plex**");
            if (plex.isLoaded)
            {
                var pi = plex.items.First();
                var plexdata = plex.getData(pi.Key);
                foreach (var pd in plexdata)
                {
                    loggerFC.Info($"\tkey=<{pd.Key}>\ttype={pd.Value.GetType()}\texample={pd.Value}");
                }

            }
            loggerFC.Info(new string('#',40));

                    
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
            var sw = new Stopwatch();
            sw.Start();

            if (torrents == null || torrents.Count == 0)
            {
                loggerFC.Info("No torrents to process.");
                return;
            }

            int torrCount = torrents.Count;
            int done = 0;

            var tasks = torrents!
                .Where(t => t != null)
                .Select(async torrent =>
                {
                    var T = Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrent));


                    if (T == null)
                    {
                        return;
                    }

                    foreach (var auto in Autos)
                    {
                        await auto.Process(T, verbose);
                    }

                    // can also do it like this
                    /*
                    var autoTasks = Autos.Select(auto => auto.Process(T, verbose));
                    await Task.WhenAll(autoTasks);
                    */

                    // increment and print progress
                    Interlocked.Increment(ref done);
                    await PrintProgress(done, torrCount);
                });

            await Task.WhenAll(tasks);
            loggerFC.Info("\nProcessing completed.");

            sw.Stop();

            if (verbose)
            {
                foreach (var auto in Autos)
                {
                    loggerFC.Info(
@$"--------------------
Auto: {auto.GetType()}
Criteria: {auto.criteria}
Success: {auto.SuccessCount}
Failure (to meet critera): {auto.FailureCount}
Error: {auto.ErrorCount}
--------------------"
                        );
                }
            }

            loggerFC.Info("**REPORT**");
            double total_AXT = Autos.Count * torrents.Count;
            loggerFC.Info($"total (Autos*Torrents): {total_AXT:F2}");
            loggerFC.Info($"time: {sw.Elapsed.TotalMilliseconds:F4} ms");
            loggerFC.Info($"{total_AXT / sw.Elapsed.TotalMilliseconds:F4} per ms");
            loggerFC.Info($"{total_AXT/sw.Elapsed.TotalSeconds:F2} per sec");

            
        }



        private async Task PrintProgress(int completed, int total)
        {
            double percent = (double)completed / total;
            int barSize = 40; // number of chars in the bar
            int filled = (int)(percent * barSize);

            string bar = new string('#', filled).PadRight(barSize);

            Console.Write($"\r[{bar}] {percent:P2}   ");
        }

        // private void PrintProgress(int completed, int total, long timems)
        // {
        //     double percent = (double)completed / total;
        //     int barSize = 40; // number of chars in the bar
        //     int filled = (int)(percent * barSize);

        //     string bar = new string('#', filled).PadRight(barSize);

        //     try
        //     {
        //         Console.SetCursorPosition(0, Console.CursorTop);
        //         Console.Write($"[{bar}] {percent:P2} {timems:F0}ms   ");
        //     }
        //     catch
        //     {
        //         Console.WriteLine($"[{bar}] {percent:P2} {timems:F0}ms");
        //     }
        // }

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
