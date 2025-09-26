/*
* ============================================================================
*  Project:      qbt_auto
*  File:         AutoCat.cs
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
    class AutoCat : AutoBase
    {
        //this is in the base class
        // public string criteria = "";
        public string category = "";

        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");

        public AutoCat(
            string category,
            string criteria,
            QBittorrentClient qbtClient,
            Plex plex,
            Dictionary<string, object> globalDicts
            )
            : base(qbtClient, plex, globalDicts)
        {
            this.category = category;
            this.criteria = criteria;
        }

        /// <summary>
        /// processes the torrent using the auto
        /// </summary>
        /// <param name="T"></param>
        /// <param name="Dict"></param>
        public override async Task Process(Dictionary<string, object> T, bool verbose)
        {
            var plexdata = plex.getData(T["ContentPath"].ToString() ?? "");

            Dictionary<string, object> Dict = new Dictionary<string, object>();
            Dict = Dict.Concat(globalDicts).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
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
                ? category
                : $"{parentDir}{sep}{category}";

            string logString = !verbose ? $"{T["Name"]} {category}" : $"Name:{T["Name"]}\nHash:{T["Hash"]?.ToString()}\ncategory:{category}\ncriteria{criteria}";

            bool? b = Evaluate(Dict, logString);
            if (b is null)
            {
                return;
            }

            if (b == true)
            {

                if (currentCategory != category)
                {
                    // AutomaticTorrentManagement should move the file to the correct location
                    await qbt.SetAutomaticTorrentManagementAsync(T["Hash"].ToString(), true);
                    await qbt.SetTorrentCategoryAsync(T["Hash"].ToString(), category);
                    logger.Info($"SetCategory :: {T["Name"]} => {category}");


                    //if it's done downloading, we will move the location
                    /*
                    if (Progress.Equals(1.0))
                    {

                        await qbt.SetLocationAsync(T["Hash"].ToString(), newLocation);

                        logger.Info($"MovedTorrent :: {T["Name"]} => {newLocation}");
                    }
                    else
                    {


                    }
                    */
                }

            }
            else if (b == false)
            {
                //do nothing
            }

        }
        
        

    }

}
