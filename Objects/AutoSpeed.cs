/*
* ============================================================================
*  Project:      qbt_auto
*  File:         AutoSpeed.cs
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

using Utils;
using QBittorrent.Client;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using System.Threading.Tasks;

namespace QbtAuto
{
    class AutoSpeed : AutoTorrentRuleBase
    {
        //this is in the base class
        /*
        public string Name = "";
        public string Type = "";
        public string Criteria = "";
        */
        public long UploadSpeed = -1;
        public long DownloadSpeed = -1;

        public List<string> ulsHashes = new List<string>();
        public List<string> dlsHashes = new List<string>();

        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");

        public AutoSpeed(
            string name,
            long uploadSpeed,
            long downloadSpeed,
            string criteria,
            ref QBittorrentClient qbtClient,
            ref Plex plex,
            ref Dictionary<string, object> globalDict,
            string type = "AutoSpeed"
            )
            : base(ref qbtClient,ref plex,ref globalDict)
        {
            this.Name               = name;
            this.Type               = type;
            this.UploadSpeed        = uploadSpeed * 1024;
            this.DownloadSpeed      = downloadSpeed * 1024;
            this.Criteria           = criteria;
        }
        
        public override string getReport()
        { 
            return @$"
--------------------
Name: {this.Name} 
Type: {this.Type}
UploadSpeed: {this.UploadSpeed}
DownloadSpeed: {this.DownloadSpeed}
Criteria: {this.Criteria}
Success: {this.SuccessCount}
Failure (to meet critera): {this.FailureCount}
Error: {this.ErrorCount}
--------------------";
        }

        /// <summary>
        /// processes the torrent using the auto
        /// </summary>
        /// <param name="T"></param>
        /// <param name="Dict"></param>
        public override async Task Process(
            Dictionary<string, object> T,
            bool verbose = false,
            bool dryRun = false
            )
        {
            var plexdata = plex.getData(T["ContentPath"].ToString() ?? "");

            Dictionary<string, object> Dict = new Dictionary<string, object>();
            Dict = Dict.Concat(globalDict).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            Dict = Dict.Concat(T).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            Dict = Dict.Concat(plexdata).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            string logString = $@"
TorrentName: {T["Name"]}
TorrentHash: {T["Hash"]}
Name: {Name}
Type: {Type}
UploadSpeed: {UploadSpeed/1024:F2}Kb
DownloadSpeed: {DownloadSpeed/1024:F2}Kb
Criteria: {Criteria}
";

            if (verbose)
            {
                logger.Info(logString);
            }

            bool? b = Evaluate(Dict, logString);
            if (dryRun)
            {
                logger.Info($"{logString}\nResult was {b} | DryRun is enabled, no changes will be made.");
                return;
            }
            
            if (b is null)
            {
                return;
            }

            if (b == false)
            {
                return;
            }

            if (b == true)
            {
                if (UploadSpeed >= 0)
                {
                    await qbt.SetTorrentUploadLimitAsync(T["Hash"].ToString(), UploadSpeed);
                    // ulsHashes.Add(T["Hash"].ToString() ?? "");
                    logger.Info($"Set uploadSpeed :: {T["Name"]} => {UploadSpeed} | {logString}");
                }

                if (DownloadSpeed >= 0)
                {
                    await qbt.SetTorrentDownloadLimitAsync(T["Hash"].ToString(), DownloadSpeed);
                    // dlsHashes.Add(T["Hash"].ToString() ?? "");
                    logger.Info($"Set downloadSpeed :: {T["Name"]} => {DownloadSpeed} | {logString}");
                }
            }
            
        }

    }

}
