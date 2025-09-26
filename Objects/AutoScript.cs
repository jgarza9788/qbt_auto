/*
* ============================================================================
*  Project:      qbt_auto
*  File:         AutoScript.cs
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
    class AutoScript : AutoBase
    {
        //this is in the base class
        // public string criteria = "";
        public string name = "";
        public string directory = "";
        public string shebang = "";
        public string script = "";
        public long timeout = 100;



        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");

        public AutoScript(
            string name,
            string directory,
            string shebang,
            string script,
            long timeout,
            string criteria,
            QBittorrentClient qbtClient,
            Plex plex,
            Dictionary<string, object> globalDicts
            )
            : base(qbtClient, plex, globalDicts)
        {
            this.name       = name;
            this.directory  = directory;
            this.shebang    = shebang;
            this.script     = script;
            this.timeout    = timeout;
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

            Dictionary<string, object> Dict = new Dictionary<string, object>();
            Dict = Dict.Concat(globalDicts).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            Dict = Dict.Concat(T).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            Dict = Dict.Concat(plexdata).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            //local _directory variable
            string _directory = Replacer(directory, Dict);
            char sep = _directory.Contains('\\') ? '\\' : '/';
            _directory = _directory + sep;

            //local _shebang variable 
            string _shebang = Replacer(shebang,Dict);
            if (_shebang == "" && OperatingSystem.IsWindows())
            {
                _shebang = "cmd.exe";
            }
            else if (_shebang == "" && !OperatingSystem.IsWindows())
            { 
                _shebang = "/bin/bang";
            }

            //local _script variable
            string _script = Replacer(script, Dict);

            string logString = !verbose ? $"{T["Name"]} {name}" : $"\ntorrent{T["Name"]}\nname:{name}\ndirectory:{directory}\ncriteria:{criteria}\nshebang:{shebang}\nscript:{script}";

            bool? b = Evaluate(Dict, logString);
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
                    if (File.Exists($"{directory}{sep}{name}"))
                    {
                        logger.Warn($"The Script has been ran for this item.\ndelete \"{directory}{sep}{name}\" to allow a re-run of this script {logString}");
                    }
                    else
                    {
                        var r = await Utils.Cmd.SheBangCmdAsync(shebang, script, directory, (int)timeout);
                        logger.Info($"{logString}\n{r.ExitCode}|{r.StdOut}|{r.StdErr}\n{logString}");
                        await File.WriteAllTextAsync($"{directory}{sep}{name}", "");
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, logString);
                }

            }


        }
        
        

    }

}
