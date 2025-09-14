/*
 * ============================================================================
 *  Project:      qbt_auto
 *  File:         ArgeParser.cs
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
    class ArgParser
    {
        public Dictionary<string, string> kwargs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ArgParser(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--"))
                {
                    var eq = a.IndexOf('=');
                    if (eq > 2)
                    {
                        kwargs[a[2..eq]] = a[(eq + 1)..];
                    }
                    else
                    {
                        var key = a[2..];
                        var val = (i + 1 < args.Length && !args[i + 1].StartsWith("-")) ? args[++i] : "true";
                        kwargs[key] = val;
                    }
                }
                else if (a.StartsWith("-"))
                {
                    var key = a[1..];
                    var val = (i + 1 < args.Length && !args[i + 1].StartsWith("-")) ? args[++i] : "true";
                    kwargs[key] = val;
                }
            }
        }

        public void print()
        {
            foreach (var entry in kwargs)
            {
                Console.WriteLine($"{entry.Key} {entry.Value}");
            }   
        }

        public bool has(string k)
        {
            return kwargs.ContainsKey(k);
        }

        public bool has(string[] k)
        { 
            for (int i = 0; i < k.Length; i++)
            {
                if (has(k[i]))
                {
                    return true;
                }
            }
            return false;
        }

        public string? get(string k, string? def = null)
        {
            if (has(k))
            {
                return kwargs[k];
            }
            return def;
        }

        public string? get(string[] k, string? def = null)
        {
            for (int i = 0; i < k.Length; i++)
            {
                if (has(k[i]))
                {
                    return kwargs[k[i]];
                }
            }
            return def;
        }

    }
}    
