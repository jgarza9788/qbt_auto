/*
 * ============================================================================
 *  Project:      qbt_auto
 *  File:         helpExampleFiles.cs
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


using Json5Core;
using QBittorrent.Client;


namespace Utils
{
    public static class helpExampleFiles
    {
        private static NLog.Logger loggerFC = NLog.LogManager.GetLogger("LoggerFC");
        private static NLog.Logger loggerF = NLog.LogManager.GetLogger("LoggerF");
        private static NLog.Logger loggerC = NLog.LogManager.GetLogger("LoggerC");


        public static void exampleKeys(
            Dictionary<string, object>? torrentData,
            Dictionary<string, object>? driveData,
            Dictionary<string, object>? plexdata
            )
        {
            string Keys = "Source,Key,Type,Example\n";
            //qbittorrent data
            try
            {
                foreach (var entry in torrentData!)
                {
                    Keys += $"qbittorrent,<{entry.Key}>,{entry.Value?.GetType().ToString()},{entry.Value?.ToString()}\n";
                }
            }
            catch (Exception Ex)
            {
                loggerF.Error(Ex);
                loggerC.Error("**ERROR** issue loading drive data");
            }
            //drive data
            try
            {
                foreach (var entry in driveData!)
                {
                    Keys += $"drives,<{entry.Key}>,{entry.Value?.GetType().ToString()},{entry.Value?.ToString()}\n";
                }
            }
            catch (Exception Ex)
            {
                loggerF.Error(Ex);
                loggerC.Error("**ERROR** issue loading drive data");
            }

            //plex items 
            try
            {
                foreach (var pd in plexdata!)
                {
                    // loggerFC.Info($"\tkey=<{pd.Key}>\ttype={pd.Value?.GetType()}\texample={pd.Value}");
                    Keys += $"plex,<{pd.Key}>,{pd.Value?.GetType().ToString()},{pd.Value?.ToString()}\n";
                }
            }
            catch (Exception Ex)
            {
                loggerF.Error(Ex);
                loggerC.Error("**ERROR** issue loading drive data");
            }
            //write to file
            File.WriteAllText("exampleKeys.csv", Keys);
            loggerFC.Info("❗ See exampleKeys.csv for a list of keys you can use in your criteria");

        }

        public static void exampleConfig()
        {
            loggerFC.Info("Let's save a config for you.");

            string exampleConfig = @"
{
    //optional - provide connection data in config
    ""qbt"": {
        ""host"": ""http://###.###.#.###:####"",
        ""user"": ""?????"",
        ""pwd"": ""*****""
    },
    //plex - optional
    ""plex"": {
        ""url"": ""http://###.###.#.###:32400"",
        ""user"": ""?????"",
        ""pwd"": ""*****""
    },
    ""AutoTorrentRules"": [
        // ───────────── AutoTag (from autoTags) ─────────────  
        {
        ""Name"": ""Tag_SmallFile"",
        ""Type"": ""AutoTag"",
        ""Tag"": ""small_file"",
        ""Criteria"": ""(<Size> < 1073741824)""
        },
        {
        ""Name"": ""Tag_MediumFile"",
        ""Type"": ""AutoTag"",
        ""Tag"": ""medium_file"",
        ""Criteria"": ""(<Size> >= 1073741824) && (<Size> < 10737418240)""
        },
        {
        ""Name"": ""Tag_LargeFile"",
        ""Type"": ""AutoTag"",
        ""Tag"": ""large_file"",
        ""Criteria"": ""(<Size> >= 10737418240)""
        },
        {
        ""Name"": ""Tag_Inactive_30d"",
        ""Type"": ""AutoTag"",
        ""Tag"": ""inactive_30d"",
        ""Criteria"": ""daysAgo(\""<LastActivityTime>\"") >= 30.0""
        },
        {
        ""Name"": ""Tag_Inactive_90d"",
        ""Type"": ""AutoTag"",
        ""Tag"": ""inactive_90d"",
        ""Criteria"": ""daysAgo(\""<LastActivityTime>\"") >= 90.0""
        },
        {
        ""Name"": ""Tag_OldMovie"",
        ""Type"": ""AutoTag"",
        ""Tag"": ""old_movie"",
        ""Criteria"": ""contains(\""<Category>\"", \""Movies\"") && daysAgo(\""<AddedOn>\"") >= 365.0""
        },
        {
        ""Name"": ""Single_Episode"",
        ""Type"": ""AutoTag"",
        ""Tag"": ""Single_Episode"",
        ""Criteria"": ""match(\""<Name>\"", \""S[0-9][0-9]E[0-9][0-9]\"")""
        },
        {
            ""Name"": ""Tag_NoViews_OnPlex"",
            ""Type"": ""AutoTag"",
            ""Tag"": ""NoViews"",
            ""Criteria"": ""(<plex_viewCount> == 0)""
        },
        // ---------- AutoCategory ----------
        {
            ""Name"": ""Category_CamMovies"",
            ""Type"": ""AutoCategory"",
            ""Category"": ""Cam_Movies"",
            ""Criteria"": ""(\""Movies\"" == \""<Category>\"") && match(\""<Name>\"", \""(\\\\.|-|\\\\s)(CAM|HDCAM|TS|HDTS|TELESYNC)(\\\\.|-|\\\\s)\"")""
        },
        // ---------- AutoScript ----------
        {
        ""Name"": ""Script_UnzipDone"",
        ""Type"": ""AutoScript"",
        ""Criteria"": ""(\""<Progress>\"" == \""1\"") && ((\""Movies\"" == \""<Category>\"") || (\""Shows\"" == \""<Category>\"")) && match(\""<ContentPath>\"",\""(Shows|Movies)\"")"",
        ""RunDir"": ""<ContentPath>"",
        ""Shebang"": ""/bin/bash"",
        ""Script"": ""unrar x -o- *.rar"", //requires unrar to be installed
        ""Timeout"": 3000
        },
        {
            //adjust permissions to file(s) on linux
            ""Name"": ""chmod.done"",
            ""Type"": ""AutoScript"",
            ""Criteria"": ""(\""<Progress>\"" == \""1\"") && match(\""<Category>\"",\""(Shows|Movies)\"") && match(\""<ContentPath>\"",\""(Shows|Movies)\"")"",
            ""RunDir"": ""<ContentPath>/.."",
            ""Shebang"": ""/bin/bash"",
            ""Script"": ""chmod 775 . -R"",
            ""Timeout"": 10  //in Seconds
        },
        // ---------- AutoMove ----------
        {
            ""Name"": ""Move_ToH00_FromS00_Stale_ShowsMovies"",
            ""Type"": ""AutoMove"",
            ""Path"": ""/media/jgarza/H00/Torrents/<Category>"",
            ""Criteria"": "" (</media/jgarza/H00_PercentUsed> < 0.9 ) && (<ActiveTime>/864000000000 >= 14.0) && ( daysAgo(\""<LastActivityTime>\"") >= 3.0) && (daysAgo(\""<LastSeenComplete>\"") >= 14.0) && match(\""<Category>\"",\""(Shows|Movies)\"") && match(\""<SavePath>\"",\""S00\"") ""
        },
        // ---------- AutoSpeed ----------
        /*
        value in kb
        0 is unlimited
        -1 is null or skip 
        */
        {
        ""Name"": ""Speed_Unlimited_ShowsMovies"",
        ""Type"": ""AutoSpeed"",
        ""UploadSpeed"": 0,
        ""UownloadSpeed"": 0,
        ""Criteria"": ""match(\""<Category>\"",\""(Shows|Movies)\"")""
        },
        {
        ""Name"": ""Slow_Down_For_DriveFull_S01"",
        ""Type"": ""AutoSpeed"",
        ""UploadSpeed"": -1, //unlimited
        ""DownloadSpeed"": 1, //1KB/s
        ""Criteria"": ""(</media/jgarza/S01_FreeSizeGB> < 1.0) && ( match(\""<SavePath>\"",\""S01\"") )""
        //^this will not the be name of your drive ...  run with the -v 1, then read the exampleKeys.csv to see what your drive is named
        },
        {
        ""Name"": ""Unlimited_Down_For_S01"",
        ""Type"": ""AutoSpeed"",
        ""UploadSpeed"": -1,  //unlimited
        ""DownloadSpeed"": -1, //unlimited
        ""Criteria"": ""(</media/jgarza/S01_FreeSizeGB> > 1.0) && ( match(\""<SavePath>\"",\""S01\"") )""
        //^this will not the be name of your drive ...  run with the -v 1, then read the exampleKeys.csv to see what your drive is named
        },
    ]
}
";
            File.WriteAllText("exampleConfig.json", exampleConfig);
        }
    }
}
