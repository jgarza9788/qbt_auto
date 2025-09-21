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
    class AutoSpeed : AutoBase
    {
        //this is in the base class
        // public string criteria = "";
        public long uploadSpeed = -1;
        public long downloadSpeed = -1;

        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");

        public AutoSpeed(
            long uploadSpeed,
            long downloadSpeed,
            string criteria,
            QBittorrentClient qbtClient,
            Plex plex,
            List<Dictionary<string, object>> globalDicts
            )
            : base(qbtClient, plex, globalDicts)
        {
            this.uploadSpeed        = uploadSpeed;
            this.downloadSpeed      = downloadSpeed;
            this.criteria           = criteria;
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

            long _uploadSpeed = uploadSpeed * 1024;
            long _downloadSpeed = downloadSpeed * 1024;

            string logString = !verbose ? $"{T["Name"]}" : $"Name:{T["Name"]}\nuploadSpeed:{uploadSpeed}\ndownloadSpeed:{downloadSpeed}\ncriteria{criteria}";

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
                if (_uploadSpeed >= 0)
                { 
                    await qbt.SetTorrentUploadLimitAsync(T["Hash"].ToString(), _uploadSpeed);
                    logger.Info($"Set uploadSpeed :: {T["Name"]} => {_uploadSpeed} | {logString}");  
                }
                
                if (_downloadSpeed >= 0)
                { 
                    await qbt.SetTorrentDownloadLimitAsync(T["Hash"].ToString(), _downloadSpeed);
                    logger.Info($"Set downloadSpeed :: {T["Name"]} => {_downloadSpeed} | {logString}"); 
                }
            }
        }
        
        

    }

}
