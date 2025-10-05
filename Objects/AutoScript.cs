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
    class AutoScript : AutoTorrentRuleBase
    {
        //this is in the base class
        /*
        public string Name = "";
        public string Type = "";
        public string Criteria = "";
        */
        public string RunDir = "";
        public string Shebang = "";
        public string Script = "";
        public long Timeout = 500;

        // public bool CreateDoneFile = true;

        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");

        public AutoScript(
            string name,
            string runDir,
            string shebang,
            string script,
            long timeout,
            string criteria,
            ref QBittorrentClient qbtClient,
            ref Plex plex,
            ref Dictionary<string, object> globalDict,
            // bool createDoneFile = true,
            string type = "AutoScript"
            )
            : base(ref qbtClient, ref plex, ref globalDict)
        {
            this.Name = name;
            this.Type = type;
            this.RunDir = runDir;
            this.Shebang = shebang;
            this.Script = script;
            this.Timeout = timeout;
            this.Criteria = criteria;
            // this.CreateDoneFile = createDoneFile;
        }

        public override string getReport()
        { 
            return @$"
--------------------
Name: {this.Name} 
Type: {this.Type}
RunDir: {this.RunDir}
Shebang: {this.Shebang}
Script: {this.Script}
Timeout: {this.Timeout}
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

            //local _directory variable
            string _runDir = Replacer(RunDir, Dict);
            char sep = _runDir.Contains('\\') ? '\\' : '/';
            _runDir = _runDir + sep;

            //local _shebang variable 
            string _shebang = Replacer(Shebang, Dict);
            if (_shebang == "" && OperatingSystem.IsWindows())
            {
                _shebang = "cmd.exe";
            }
            else if (_shebang == "" && !OperatingSystem.IsWindows())
            {
                _shebang = "/bin/bang";
            }

            //local _script variable
            string _script = Replacer(Script, Dict);

            string logString = $@"
TorrentName: {T["Name"]}
TorrentHash: {T["Hash"]}
Name: {Name}
Type: {Type}
Directory: {_runDir}
SheBang: {_shebang}
Script: {_script}
Criteria: {Criteria}
";

            if (verbose)
            {
                logger.Info(logString);
            }

            
            if (!Directory.Exists(_shebang) && !File.Exists(_shebang))
            {
                logger.Warn($"Bad SheBang,\n{logString}");
                return;
            }

            if (!Directory.Exists(_runDir))
            {
                logger.Warn($"Bad RunDir,\n{logString}");
                return;
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
                try
                {
                    if (File.Exists($"{_runDir}{sep}{Name}"))
                    {
                        logger.Warn($"The Script has been ran for this item.\ndelete \"{_runDir}{sep}{Name}\" to allow a re-run of this script {logString}");
                    }
                    else
                    {
                        var r = await Utils.Cmd.SheBangCmdAsync(_shebang, _script, _runDir, (int)Timeout);
                        logger.Info($"{logString}\n{r.ExitCode}|{r.StdOut}|{r.StdErr}\n{logString}");
                        await File.WriteAllTextAsync($"{_runDir}{sep}{Name}", "");

                        // if (CreateDoneFile)
                        // {
                        //     await File.WriteAllTextAsync($"{_runDir}{sep}{Name}", "");
                        // }
                        
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
