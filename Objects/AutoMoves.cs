/*
* ============================================================================
*  Project:      qbt_auto
*  File:         AutoMoves.cs
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
    class AutoMove : AutoTorrentRuleBase
    {
        //this is in the base class
        /*
        public string Name = "";
        public string Type = "";
        public string Criteria = "";
        */
        public string Path = "";

        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");

        public AutoMove(
            string name,
            string path,
            string criteria,
            ref QBittorrentClient qbtClient,
            ref Plex plex,
            ref Dictionary<string, object> globalDict,
            string type = "AutoMove"
            )
            : base(ref qbtClient, ref plex, ref globalDict)
        {
            this.Name = name;
            this.Type = type;
            this.Path = path;
            this.Criteria = criteria;
        }

        public override string getReport()
        {
            return @$"
--------------------
Name: {this.Name} 
Type: {this.Type}
Path: {this.Path}
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

            //lcoal _path variable
            string _path = Replacer(Path, Dict);

            string logString = $@"
TorrentName: {T["Name"]}
TorrentHash: {T["Hash"]}
AutoName: {Name}
AutoType: {Type}
TargetPath: {_path}
Criteria: {Criteria}
";


            if (!Directory.Exists(_path))
            {
                logger.Warn($"path does not exists,\n{logString}");
                return;
            }

            bool? b = Evaluate(Dict, logString, verbose);
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
                try
                {
                    //unable to do these in bulk due to the different paths
                    await qbt.SetAutomaticTorrentManagementAsync(T["Hash"].ToString(), false);
                    await qbt.SetLocationAsync(T["Hash"].ToString(), _path);
                    logger.Info($"MovedTorrent :: {T["Name"]} => {_path} | {logString}");
                }
                catch (Exception ex)
                {
                    logger.Error(ex, logString);
                    return;
                }
            }
        }


    }

}
