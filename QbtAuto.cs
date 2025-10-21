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
using NLog.LayoutRenderers.Wrappers;
using NLog;

namespace QbtAuto
{
    class QbtAuto
    {

        public static string Version = "v0.4.2";

        #region Inputs
        private string? URL;
        private string? USER;
        private string? Password;
        public string? ConfigPath;
        public bool verbose = false;
        #endregion

        //list of autos
        List<AutoTorrentRuleBase> Autos = new List<AutoTorrentRuleBase>();
        Dictionary<string, object> globalDict = new Dictionary<string, object>();

        //Clients 
        public QBittorrentClient? qbt;
        public Plex plex = new Plex();


        public DateTime LastRefreshTime;
        //variables to store DriveData and Torrent Data
        public Dictionary<string, object> driveData = new Dictionary<string, object>();
        public IReadOnlyList<TorrentInfo>? torrents;

        //logger 
        // public static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        private static NLog.Logger loggerFC = NLog.LogManager.GetLogger("LoggerFC");
        private static NLog.Logger loggerF = NLog.LogManager.GetLogger("LoggerF");
        private static NLog.Logger loggerC = NLog.LogManager.GetLogger("LoggerC");

        public bool DryRun = false;

        public bool IsSetUp = false;

        public QbtAuto(string[] args)
        {
            //set Console to unicode (optional)
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Title();

            //processing Args
            processArgs(args);


            DataObject config = new DataObject(ConfigPath ?? "");
            if (config.data.Count == 0)
            {
                helpExampleFiles.exampleConfig();
                loggerC.Info("❌ Please fill out the exampleConfig.json and run again.");
                return;
            }
            else
            {
                loggerFC.Info("✅ Config loaded.");
            }
                

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
                plex.user = getData(plex_login_data, ["user", "u"]) as string ?? "";
                plex.pwd = getData(plex_login_data, ["pwd", "password", "p"]) as string ?? "";

                try
                {
                    plex.LoadAsync(forceReload: true).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    loggerF.Error(ex, "unable to get data from plex, see error");
                    loggerC.Error("❌ unable to get data from plex, see error");
                }
            }

            //maybe we'll just ask the user
            if (EnsureParametersValid())
            {
                loggerFC.Info("✅ Parameters valid.");
            }
            else
            {
                loggerC.Info("❌ Please fill out the exampleConfig.json and run again.");
                return;
            }

            // i can print this later 
            // loggerFC.Info(getParameterInfo());

            //refresh data
            refreshData();
            if (qbt == null)
            {
                loggerFC.Error("qbt is null, but it should have been set to an instance");
                return;
            }

            #region CreateAutoObjects

            IEnumerable<object> ATRs = config.getValue("AutoTorrentRules") as IEnumerable<object> ?? new List<object>();
            createAutoObjects(ATRs);

            #endregion


            #region verbose_for_testing
            if (verbose)
            {
                if (torrents != null)
                {

                    helpExampleFiles.exampleKeys(
                        Json5.Deserialize<Dictionary<string, object>>(Json5.Serialize(torrents[0])),
                        driveData,
                        plex.getData(plex.items.First().Key)
                        );
                }
            }
            #endregion


            IsSetUp = true;

        }

        /// <summary>
        /// process the args into the different variables
        /// </summary>
        /// <param name="args"></param>
        public void processArgs(string[] args)
        {
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

            string? dr = AP.get(["dryrun", "dry_run", "dry", "dr", "dry-run"], "") ?? "";
            dr = dr.Trim();
            if (
                dr.Contains('1', StringComparison.OrdinalIgnoreCase)
                || dr.Contains("TRUE", StringComparison.OrdinalIgnoreCase)
                || dr.Contains("YES", StringComparison.OrdinalIgnoreCase)
                || dr.Contains("ON", StringComparison.OrdinalIgnoreCase)
                || dr.Contains("T", StringComparison.OrdinalIgnoreCase)
                )
            {
                DryRun = true;
            }

            ConfigPath = string.IsNullOrWhiteSpace(ConfigPath) ? "" : ConfigPath;

            loggerFC.Info("✅ Args processed.");
        }


        /// <summary>
        /// creates the Auto objects from the config data
        /// </summary>
        /// <param name="autoTorrentRules"></param>
        public void createAutoObjects(IEnumerable<object> autoTorrentRules)
        {
            // List<Type> autoTypes = TypeHelper.GetChildClasses<AutoTorrentRuleBase>();

            if (qbt == null)
            {
                loggerFC.Error("❌ qbt is null");
                return;
            }

            if (plex == null)
            {
                loggerFC.Error("❌ plex is null");
                return;
            }

            if (globalDict == null)
            {
                loggerFC.Error("❌ globalDict is null");
                return;
            }

            foreach (IDictionary<string, object> ATR in autoTorrentRules)
            {
                string type = (ATR["Type"].ToString() ?? "").ToUpper();

                if (type == "AutoTag".ToUpper())
                {
                    Autos.Add(
                        new AutoTag(
                        name: ATR["Name"].ToString() ?? "",
                        tag: ATR["Tag"].ToString() ?? "",
                        criteria: ATR["Criteria"].ToString() ?? "",
                        qbtClient: ref qbt,
                        plex: ref plex,
                        globalDict: ref globalDict
                        )
                    );
                }
                else if (type == "AutoCategory".ToUpper())
                {
                    Autos.Add(
                        new AutoCategory(
                        name: ATR["Name"].ToString() ?? "",
                        category: ATR["Category"].ToString() ?? "",
                        criteria: ATR["Criteria"].ToString() ?? "",
                        qbtClient: ref qbt,
                        plex: ref plex,
                        globalDict: ref globalDict
                        )
                    );
                }
                else if (type == "AutoScript".ToUpper())
                {
                    Autos.Add(
                        new AutoScript(
                        name: ATR["Name"].ToString() ?? "",
                        runDir: ATR["RunDir"].ToString() ?? "",
                        shebang: ATR["Shebang"].ToString() ?? "",
                        script: ATR["Script"].ToString() ?? "",
                        timeout: (long)(ATR["Timeout"] ?? 500),
                        criteria: ATR["Criteria"].ToString() ?? "",
                        qbtClient: ref qbt,
                        plex: ref plex,
                        globalDict: ref globalDict
                        )
                    );
                }
                else if (type == "AutoMove".ToUpper())
                {
                    Autos.Add(
                        new AutoMove(
                        name: ATR["Name"].ToString() ?? "",
                        path: ATR["Path"].ToString() ?? "",
                        criteria: ATR["Criteria"].ToString() ?? "",
                        qbtClient: ref qbt,
                        plex: ref plex,
                        globalDict: ref globalDict
                        )
                    );
                }
                else if (type == "AutoSpeed".ToUpper())
                {
                    Autos.Add(
                        new AutoSpeed(
                        name: ATR["Name"].ToString() ?? "",
                        uploadSpeed: (long)(ATR["UploadSpeed"] ?? -1),
                        downloadSpeed: (long)(ATR["DownloadSpeed"] ?? -1),
                        criteria: ATR["Criteria"].ToString() ?? "",
                        qbtClient: ref qbt,
                        plex: ref plex,
                        globalDict: ref globalDict
                        )
                    );
                }
                else
                {
                    string logString = $"AutoTorrentRule {(ATR["Name"].ToString() ?? "")} does not have a Type Value";
                    if (verbose)
                    {
                        loggerFC.Warn(logString);
                    }
                    else
                    {
                        loggerF.Warn(logString);
                    }
                }
            }
        }


        /// <summary>
        /// refreshes data from 
        /// </summary>
        public void refreshData()
        {
            if ((DateTime.Now - LastRefreshTime).Hours < 1.0)
            {
                loggerF.Info("no refresh needed");
                return;
            }
            LastRefreshTime = DateTime.Now;

            plex.LoadAsync().GetAwaiter().GetResult();
            loggerFC.Info(plex.isLoaded ? "✅ Plex loaded." : "❌ Plex not loaded.");

            driveData = Drives.getDriveData();
            loggerFC.Info((driveData.Count > 0) ? "✅ DriveData loaded." : "❌ DriveData not loaded.");

            bool pdt = pullTorrentData();
            loggerFC.Info(pdt ? "✅ TorrentData loaded." : "❌ TorrentData not loaded.");

            globalDict.Clear();
            globalDict = globalDict.Concat(driveData).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// pulls the torrent data from qbt
        /// </summary>
        /// <returns></returns>
        public bool pullTorrentData()
        {
            try
            {
                if (qbt == null)
                {
                    var httpHandler = new SocketsHttpHandler
                    {
                        MaxConnectionsPerServer = 100,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                        AutomaticDecompression = DecompressionMethods.All
                    };

                    qbt = new QBittorrentClient(new Uri(URL!), httpHandler, disposeHandler: false);
                    qbt.LoginAsync(USER!, Password!).GetAwaiter().GetResult();
                }
                torrents = qbt.GetTorrentListAsync().GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                loggerF.Error(ex, "unable to get data from qbt, see error");
                return false;
            }

        }

        /// <summary>
        /// prints the title, and version
        /// </summary>
        public void Title()
        {
            Console.Write($@"
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
    {Version}
");

        }


        public string getParameterInfo()
        {
            return @$"
URL: {URL}  
USER: {USER}
Password: ******
ConfigPath: {ConfigPath}
Plex.isLoaded: {plex.isLoaded}
verbose: {verbose}
Dry Run: {DryRun}
";
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
            if (!IsSetUp)
            {
                loggerFC.Error("QbtAuto not set up correctly, please check the parameters and config");
                return;
            }


            var sw = new Stopwatch();
            sw.Start();

            refreshData();

            if (torrents == null || torrents.Count == 0)
            {
                loggerFC.Info("No torrents to process.");
                return;
            }

            if (qbt == null)
            {
                loggerFC.Error("qbt is null");
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
                        await auto.Process(T, verbose, DryRun);
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



            loggerFC.Info("\n✅ Processing completed.");

            sw.Stop();

            if (verbose)
            {
                foreach (var auto in Autos)
                {
                    loggerFC.Info(auto.getReport());
                }
            }


            double total_AXT = Autos.Count * torrents.Count;
            loggerFC.Info(getParameterInfo());
            loggerFC.Info(@$"
**REPORT**
total (Autos*Torrents): {total_AXT:F2}
time: {sw.Elapsed.TotalMilliseconds:F4} ms
{total_AXT / sw.Elapsed.TotalMilliseconds:F4} per ms
{total_AXT / sw.Elapsed.TotalSeconds:F2} per sec
            ");

        }

        private Task PrintProgress(int completed, int total)
        {
            double percent = (double)completed / total;
            int barSize = 40; // number of chars in the bar
            int filled = (int)(percent * barSize);

            string bar = new string('#', filled).PadRight(barSize);

            Console.Write($"\r[{bar}] {percent:P2}   ");
            return Task.CompletedTask;

        }

        #endregion


        /// <summary>
        /// EnsureParametersValid
        /// </summary>
        /// <returns></returns>
        private bool EnsureParametersValid()
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

            int attempts = 0;
            int maxAttempts = 3;
            while ((string.IsNullOrWhiteSpace(ConfigPath) || !System.IO.File.Exists(ConfigPath)) && attempts < maxAttempts)
            {
                Console.Write($"Enter ConfigPath {attempts}/{maxAttempts} : ");
                ConfigPath = Console.ReadLine();
                attempts++;
            }

            // Ask for ConfigPath if not provided or file not found
            if (string.IsNullOrWhiteSpace(ConfigPath) || !System.IO.File.Exists(ConfigPath))
            {
                helpExampleFiles.exampleConfig();
                return false;
            }

            return true;

        }


        
    }
}    
