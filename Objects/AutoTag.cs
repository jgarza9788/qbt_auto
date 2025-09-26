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
using Microsoft.CodeAnalysis.Scripting.Hosting;
using System.Threading.Tasks;

namespace QbtAuto
{
    class AutoTag : AutoBase
    {
        //this is in the base class
        // public string criteria = "";
        public string tag = "";

        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");

        public AutoTag(
            string tag,
            string criteria,
            QBittorrentClient qbtClient,
            Plex plex,
            Dictionary<string, object> globalDicts
            )
            : base(qbtClient, plex, globalDicts)
        {
            this.tag = tag;
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


            string currentTags = T["Tags"] is IEnumerable<object> ctlist
                ? string.Join(",", ctlist)
                : T["Tags"]?.ToString() ?? "";

            string logString = !verbose ? $"{T["Name"]} {tag}" : $"Name:{T["Name"]}\nHash:{T["Hash"]}\nTag:{tag}\ncriteria:{criteria}";

            bool? b = Evaluate(Dict, logString);
            if (b is null)
            {
                return;
            }

            if (b == true)
            {
                if (!currentTags.Contains(tag))
                {
                    await AddTag(T, tag);
                    // await qbt.AddTorrentTagAsync(T["Hash"].ToString(), tag);
                    // logger.Info($"AddTag :: {T["Name"]} + {tag}");
                }
            }
            else if (b == false)
            {
                if (currentTags.Contains(tag))
                {
                    await RemoveTag(T, tag);
                    // await qbt.DeleteTorrentTagAsync(T["Hash"].ToString(), tag);
                    // logger.Info($"DeleteTag :: {T["Name"]} - {tag}");
                }
            }

        }

        /// <summary>
        /// AddTag
        /// </summary>
        /// <param name="T"></param>
        /// <param name="tag"></param>
        public async Task AddTag(Dictionary<string, object> T, string tag)
        {
            await qbt.AddTorrentTagAsync(T["Hash"].ToString(), tag);
            logger.Info($"AddTag :: {T["Name"]} + {tag}");
        }
        
        /// <summary>
        /// RemoveTag
        /// </summary>
        /// <param name="T"></param>
        /// <param name="tag"></param>
        public async Task RemoveTag(Dictionary<string, object> T, string tag)
        {
            await qbt.DeleteTorrentTagAsync(T["Hash"].ToString(), tag);
            logger.Info($"DeleteTag :: {T["Name"]} - {tag}");
        }
        
        

    }

}
