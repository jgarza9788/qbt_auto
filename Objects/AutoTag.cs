/*
* ============================================================================
*  Project:      qbt_auto
*  File:         AutoTag.cs
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


namespace QbtAuto
{
    class AutoTag : AutoTorrentRuleBase
    {
        //this is in the base class
        /*
        public string Name = "";
        public string Type = "";
        public string Criteria = "";
        */
        public string Tag = "";

        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");


        public AutoTag(
            string name,
            string tag,
            string criteria,
            ref QBittorrentClient qbtClient,
            ref Plex plex,
            ref Dictionary<string, object> globalDict,
            string type = "AutoTag"
            )
            : base(ref qbtClient, ref plex, ref globalDict)
        {
            this.Name = name;
            this.Type = type;
            this.Tag = tag;
            this.Criteria = criteria;
        }

        public override string getReport()
        {
            return @$"
--------------------
Name: {this.Name} 
Type: {this.Type}
Tag: {this.Tag}
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


            string currentTags = T["Tags"] is IEnumerable<object> ctlist
                ? string.Join(",", ctlist)
                : T["Tags"]?.ToString() ?? "";

            string logString = $@"
TorrentName: {T["Name"]}
TorrentHash: {T["Hash"]}
Name: {Name}
Type: {Type}
Tag: {Tag}
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

            if (b == true)
            {
                if (!currentTags.Contains(Tag))
                {
                    await qbt.AddTorrentTagAsync(T["Hash"].ToString(), Tag);
                    // logger.Info($"AddTag :: {T["Name"]} + {tag}");
                }
            }
            else if (b == false)
            {
                if (currentTags.Contains(Tag))
                {

                    await qbt.DeleteTorrentTagAsync(T["Hash"].ToString(), Tag);
                    // logger.Info($"DeleteTag :: {T["Name"]} - {tag}");
                }
            }



        }


        

    }

}
