/*
* ============================================================================
*  Project:      qbt_auto
*  File:         AutoBase.cs
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

namespace QbtAuto
{
    abstract class AutoBase
    {
        public string criteria = "";

        public int SuccessCount = 0;
        public int FailureCount = 0;
        public int ErrorCount = 0;

        public Interpreter it = new Interpreter();

        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");
        
        public QBittorrentClient qbt;
        public Plex plex;

        public List<Dictionary<string, object>> globalDicts;

        // Constructor
        protected AutoBase(QBittorrentClient qbtClient,Plex plex, List<Dictionary<string, object>> globalDicts)
        {
            this.qbt = qbtClient;
            this.plex = plex;
            this.globalDicts = globalDicts;

            this.SuccessCount = 0;
            this.FailureCount = 0;
            this.ErrorCount = 0;
            this.it = new Interpreter()
                .SetFunction("contains", (string t, string s) => t.Contains(s))
                .SetFunction("match", (string t, string p) => Regex.IsMatch(t, p))
                .SetFunction("daysAgo", (string iso) => (DateTime.UtcNow - DateTime.Parse(iso)).TotalDays)
                .SetDefaultNumberType(DefaultNumberType.Double);
        }

        public virtual async Task Process(
            Dictionary<string, object> T,
            bool verbose = false
            )
        { 
            // Example of future async code
            await Task.Delay(1); 
        }


        public bool? Evaluate(List<Dictionary<string, object>> Dicts,string logstring = "")
        {
            try
            {
                string _criteria = Replacer(criteria, Dicts);
                
                bool result = it.Eval<bool>(_criteria);
                logger.Info($"{_criteria} | {result}");

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
                
                logger.Error(ex, logstring);
                return null;
            }
        }

        public string Replacer(string String, List<Dictionary<string,object>> Dicts)//Dictionary<string, object>[] Dicts)
        {
            foreach (var Dict in Dicts)
            {
                String = Replacer(String, Dict);

                // logger.Info(String);
            }
            return String;
        } 

        public string Replacer(string String, Dictionary<string, object> Dict)
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
