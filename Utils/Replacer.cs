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
        // public static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        private static NLog.Logger logger = NLog.LogManager.GetLogger("LoggerF");

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

        /// <summary>
        /// Normalizes a dictionary of counts to a list of (title, score) tuples
        /// </summary>
        /// <param name="counts"></param>
        /// <returns></returns> 
        public static List<(string Title, double Score)> Normalize(Dictionary<string,float> counts)
        {
            var list = counts.Select(kv => kv.Value).ToList();
            float min = list.Min<float>();
            float max = list.Max<float>();

            return counts
                .Select(kv => (
                    Title: kv.Key,
                    Score: max == min ? 1.0 : (double)(kv.Value - min) / (max - min)
                ))
                .Select(x => (x.Title, x.Score))
                .ToList();
        }

        public static List<(string Title, double Score)> NormalizeQuantile(Dictionary<string, float> counts)
        {
            if (counts == null) throw new ArgumentNullException(nameof(counts));
            if (counts.Count == 0) return new List<(string Title, double Score)>();

            // Sort by value ascending
            var items = counts
                .Select(kv => (Title: kv.Key, Value: kv.Value))
                .OrderBy(x => x.Value)
                .ToList();

            int n = items.Count;
            if (n == 1)
                return new List<(string Title, double Score)> { (items[0].Title, 1.0) };

            // Assign quantile score by rank, evenly spaced in [0,1]
            // Ties get the midrank (average rank of the tie block)
            var result = new List<(string Title, double Score)>(n);

            int i = 0;
            while (i < n)
            {
                int j = i;
                float v = items[i].Value;

                // find tie block [i, j]
                while (j + 1 < n && items[j + 1].Value.Equals(v))
                    j++;

                // ranks are 0..n-1; midrank is average of i..j
                double midRank = (i + j) / 2.0;

                // Normalize rank to [0,1]
                double score = midRank / (n - 1);

                // If you want "highest value => 1.0", this already does that
                // because higher values have higher ranks.

                for (int k = i; k <= j; k++)
                    result.Add((items[k].Title, score));

                i = j + 1;
            }

            return result;
        }
    
    }

}
