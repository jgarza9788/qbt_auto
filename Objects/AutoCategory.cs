/*
* ============================================================================
*  Project:      qbt_auto
*  File:         AutoCategory.cs
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
    class AutoCategory : AutoTorrentRuleBase
    {
        //this is in the base class
        /*
        public string Name = "";
        public string Type = "";
        public string Criteria = "";
        */
        public string Category = "";

        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");

        public AutoCategory(
            string name,
            string category,
            string criteria,
            ref QBittorrentClient qbtClient,
            ref Plex plex,
            ref Dictionary<string, object> globalDict,
            string type = "AutoCategory"
            )
            : base(ref qbtClient, ref plex, ref globalDict)
        {
            this.Name = name;
            this.Type = type;
            this.Category = category;
            this.Criteria = criteria;
        }

        public override string getReport()
        {
            return @$"
--------------------
Name: {this.Name} 
Type: {this.Type}
Category: {this.Category}
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
                ? Category
                : $"{parentDir}{sep}{Category}";

            // Log useful torrent and rule data for debugging and auditing
            string logString = $@"
TorrentName: {T["Name"]}
TorrentHash: {T["Hash"]}
AutoName: {Name}
AutoType: {Type}
CurrentCategory: {currentCategory}
TargetCategory: {Category}
Criteria: {Criteria}
Progress: {Progress}
SavePath: {SavePath}
ParentDir: {parentDir}
NewLocation: {newLocation}
";


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

            if (b == true)
            {

                if (currentCategory != Category)
                {
                    // AutomaticTorrentManagement should move the file to the correct location
                    
                    await qbt.SetAutomaticTorrentManagementAsync(T["Hash"].ToString(), true);
                    await qbt.SetTorrentCategoryAsync(T["Hash"].ToString(), Category);
                    

                    // TrueHashes.Add(T["Hash"].ToString() ?? "");
                    logger.Info($"SetCategory :: {T["Name"]} => {Category}");

                }

            }
            else if (b == false)
            {
                //do nothing
            }


        }
        

        
    }

}
