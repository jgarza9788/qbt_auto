/*
* ============================================================================
*  Project:      qbt_auto
*  File:         AutoTorrentRuleBase.cs
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
using DynamicExpresso;
using System.Data;
using System.Text.RegularExpressions;
using QBittorrent.Client;
using System.Runtime.CompilerServices;

namespace QbtAuto
{
    abstract class AutoTorrentRuleBase
    {


        public string Name = "";
        public string Type = "";
        public string Criteria = "";

        public int SuccessCount = 0;
        public int FailureCount = 0;
        public int ErrorCount = 0;

        public Interpreter it = new Interpreter();

        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");
        
        public QBittorrentClient qbt;
        public Plex plex;

        public Dictionary<string, object> globalDict;
        


        // Constructor
        protected AutoTorrentRuleBase(ref QBittorrentClient qbtClient, ref Plex plex, ref Dictionary<string, object> globalDict)
        {
            this.qbt = qbtClient;
            this.plex = plex;
            this.globalDict = globalDict;

            this.SuccessCount = 0;
            this.FailureCount = 0;
            this.ErrorCount = 0;
            this.it = new Interpreter()
                .SetFunction("contains", (string t, string s) => t.Contains(s))
                .SetFunction("match", (string t, string p) => Regex.IsMatch(t, p, RegexOptions.IgnoreCase))
                .SetFunction("daysAgo", (string iso) => (DateTime.UtcNow - DateTime.Parse(iso)).TotalDays)
                .SetDefaultNumberType(DefaultNumberType.Double);
        }

        public virtual string getReport()
        {
            return @$"
--------------------
Name: {this.Name} 
Type: {this.Type}
Criteria: {this.Criteria}
Success: {this.SuccessCount}
Failure (to meet critera): {this.FailureCount}
Error: {this.ErrorCount}
--------------------";
        }

        public virtual async Task Process(
            Dictionary<string, object> T,
            bool verbose = false,
            bool dryRun = false
            )
        {
            // Example of future async code
            await Task.Delay(1);
        }



        public bool? Evaluate(Dictionary<string, object> Dict, string logstring = "")
        {

            try
            {
                string _criteria = Replacer(Criteria, Dict);
                bool result = it.Eval<bool>(_criteria);

                if (result)
                {
                    SuccessCount++;
                }
                else
                {
                    FailureCount++;
                }

                return result;

            }
            catch (Exception ex)
            {
                ErrorCount++;
                logger.Warn(ex, logstring);
                return null;
            }
        }




        /// <summary>
        /// this one is faster at 1327 per sec
        /// </summary>
        /// <param name="criteriaString"></param>
        /// <param name="Dict"></param>
        /// <returns></returns>
        public string Replacer(string criteriaString, Dictionary<string, object> Dict)
        {
            var rx = new Regex(@"<([^<>]+)>");
            var matches = rx.Matches(criteriaString);

            foreach (Match match in matches)
            {
                string m = match.Groups[1].Value;
                // logger.Info($"{criteriaString} | {m}");
                criteriaString = criteriaString.Replace($"<{m}>", Dict[m].ToString() ?? $"** ERROR <{match.Value}> is not a key **");
            }



            return criteriaString;
        } 


        /// <summary>
        /// this one does about 1284 per sec
        /// </summary>
        /// <param name="String"></param>
        /// <param name="Dict"></param>
        /// <returns></returns>
        public string Replacer_old(string String, Dictionary<string, object> Dict)
        {

            foreach (var entry in Dict)
            {
                try
                {
                    string value = "";

                    if (entry.Value is System.Collections.IList)
                    {
                        var enumerable = (entry.Value as System.Collections.IList)?.Cast<object>() ?? new List<object>();
                        value = string.Join(",", enumerable);
                    }
                    else
                    {
                        value = entry.Value?.ToString() ?? "";
                    }

                    String = String.Replace($"<{entry.Key}>", value);

                    //return early if there are no matches
                    var matches = Regex.Matches(String, "<.*?>");
                    if (matches.Count == 0)
                    {
                        return String;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex);
                }

            }

            return String;
        }   
        
    }
}
