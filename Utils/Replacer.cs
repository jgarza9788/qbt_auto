/*
 * ============================================================================
 *  Project:      qbt_auto
 *  File:         Replacer.cs
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


namespace Utils
{
    public static class Misc
    {
        public static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Replaces string with values from dictionary 
        /// </summary>
        /// <param name="String"></param>
        /// <param name="Dict"></param>
        /// <returns></returns>
        public static string Replacer(string String, Dictionary<string, object> Dict)
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
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"**error** {entry.Key} {entry.Value?.ToString()} {String}");
                    logger.Error(ex);
                }

            }

            return String;
        }  

        /// <summary>
        /// Replaces the string with values from dictionaries
        /// </summary>
        /// <param name="String"></param>
        /// <param name="Dicts"></param>
        /// <returns></returns>
        public static string Replacer(string String, Dictionary<string, object>[] Dicts)
        {
            foreach (var Dict in Dicts)
            {
                String = Replacer(String, Dict);

                // logger.Info(String);
            }
            return String;
        }     
    }

}
