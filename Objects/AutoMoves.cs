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
    class AutoMoves : AutoBase
    {
        //this is in the base class
        // public string criteria = "";
        public string path = "";

        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");

        public AutoMoves(
            string path,
            string criteria,
            QBittorrentClient qbtClient,
            Plex plex,
            List<Dictionary<string, object>> globalDicts
            )
            : base(qbtClient, plex, globalDicts)
        {
            this.path       = path;
            this.criteria   = criteria;
        }

        /// <summary>
        /// processes the torrent using the auto
        /// </summary>
        /// <param name="T"></param>
        /// <param name="Dict"></param>
        public override async Task Process(Dictionary<string, object> T, bool verbose)
        {
            var plexdata = plex.getData(T["ContentPath"].ToString() ?? "");

            List<Dictionary<string, object>> Dicts = new List<Dictionary<string, object>>(globalDicts);
            Dicts.Add(T);
            Dicts.Add(plexdata);

            //lcoal _path variable
            string _path = Replacer(path, Dicts);

            string logString = !verbose ? $"{T["Name"]}" : $"Name:{T["Name"]}\npath:{path}\ncriteria{criteria}";

            if (!Directory.Exists(path))
            {
                logger.Warn($"path does not exists,\n{logString}");
                return;
            }

            bool? b = Evaluate(Dicts, logString);
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
                    await qbt.SetAutomaticTorrentManagementAsync(T["Hash"].ToString(), false);
                    await qbt.SetLocationAsync(T["Hash"].ToString(), path);
                    logger.Info($"MovedTorrent :: {T["Name"]} => {path} | {logString}");
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
