```
          $$\        $$\                                $$\               
          $$ |       $$ |                               $$ |              
 $$$$$$\  $$$$$$$\ $$$$$$\         $$$$$$\  $$\   $$\ $$$$$$\    $$$$$$\  
$$  __$$\ $$  __$$\\_$$  _|        \____$$\ $$ |  $$ |\_$$  _|  $$  __$$\ 
$$ /  $$ |$$ |  $$ | $$ |          $$$$$$$ |$$ |  $$ |  $$ |    $$ /  $$ |
$$ |  $$ |$$ |  $$ | $$ |$$\      $$  __$$ |$$ |  $$ |  $$ |$$\ $$ |  $$ |
\$$$$$$$ |$$$$$$$  | \$$$$  |     \$$$$$$$ |\$$$$$$  |  \$$$$  |\$$$$$$  |
 \____$$ |\_______/   \____/$$$$$$\\_______| \______/    \____/  \______/ 
      $$ |                  \______|                                      
      $$ |                                                                
      \__|
```

# qbt_auto

![Release](https://img.shields.io/github/v/release/jgarza9788/qbt_auto?include_prereleases&label=latest&color=028ffa)
![License](https://img.shields.io/badge/license-FSL-028ffa)

![Downloads](https://img.shields.io/github/downloads/jgarza9788/qbt_auto/total?label=downloads&color=028ffa)
![Last Commit](https://img.shields.io/github/last-commit/jgarza9788/qbt_auto?color=028ffa)

<!--
![Build](https://img.shields.io/github/actions/workflow/status/jgarza9788/qbt_auto/build.yml?label=build&color=028ffa)
![Tests](https://img.shields.io/github/actions/workflow/status/jgarza9788/qbt_auto/tests.yml?label=tests&color=028ffa)
-->

![C#](https://img.shields.io/badge/language-C%23-028ffa?logo=csharp)
![.NET](https://img.shields.io/badge/.NET-9.0-028ffa?logo=dotnet)
![qBittorrent](https://img.shields.io/badge/qBittorrent-automation-028ffa?logo=qbittorrent&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Linux%20%7C%20Windows-028ffa)

![Stars](https://img.shields.io/github/stars/jgarza9788/qbt_auto?style=social&color=028ffa)
![Forks](https://img.shields.io/github/forks/jgarza9788/qbt_auto?style=social&color=028ffa)
![Contributors](https://img.shields.io/github/contributors/jgarza9788/qbt_auto?color=028ffa)
![Open Issues](https://img.shields.io/github/issues/jgarza9788/qbt_auto?color=028ffa)

---

This is a qBittorrent automation tool.
It connects to a qBittorrent instance, loads a config file (config.json by default), and then automatically applies tags and categories to torrents based on rules you define in that config.


## Support 
☕[Buy Me Coffee ... please](https://buymeacoffee.com/jgarza97885)


## Requirements

* Server running qbittorrent, and access to it.

## Software Packages
```
> dotnet list package
Project 'qbt_auto' has the following package references
   [net9.0]:
   Top-level Package                              Requested     Resolved
   > DynamicExpresso.Core                         2.19.2        2.19.2
   > Json5Core                                    1.0.12        1.0.12
   > Microsoft.CodeAnalysis.CSharp.Scripting      4.14.0        4.14.0
   > NLog                                         6.0.3         6.0.3
   > QBittorrent.Client                           1.9.24285.1   1.9.24285.1
```


## Features
* tags
* category 
* running Scripts (cmd, bash, pwsh, etc )
* moving files
* change upload and download rates - speed
* plex support 

### RoadMap (features that i'll add soon) 
* request a new feature --please

## Run 
```
/path/to/qbt_auto -url http://###.###.#.###:8080 -u username -p password -c config.json
```
```
# if the connection data is in the config file
/path/to/qbt_auto -c config.json
```
### inputs
* -host -url -H (H upper case to not confuse with help)
* -u -user 
* -p -password -pwd
* -c -config -configpath
* -v -verbose
* -h -help or -?

## Run at intervals
Run at intervals by adding the command to CRON (linux), or Windows Task Scheduler.

## config.json (example)
```json
{
  //optional - provide connection data in config
  "qbt": {
    "host": "http://###.###.#.###:####",
    "user": "?????",
    "pwd": "*****"
  },
  //plex - optional
  "plex": {
    "url": "http://###.###.#.###:32400",
    "user": "?????",
    "pwd": "*****"
  },
  "AutoTorrentRules": [
    // ───────────── AutoTag (from autoTags) ─────────────
    {
      "Name": "Tag_SmallFile",
      "Type": "AutoTag",
      "Tag": "small_file",
      "Criteria": "(<Size> < 1073741824)"
    },
    {
      "Name": "Tag_MediumFile",
      "Type": "AutoTag",
      "Tag": "medium_file",
      "Criteria": "(<Size> >= 1073741824) && (<Size> < 10737418240)"
    },
    {
      "Name": "Tag_LargeFile",
      "Type": "AutoTag",
      "Tag": "large_file",
      "Criteria": "(<Size> >= 10737418240)"
    },
    {
        "Name": "ULperDay_0.00GB_0.10GB",
        "Type": "AutoTag",
        "Tag": "ULperDay_0.0GB_0.10GB",
        "Criteria": "(<Uploaded>/(<ActiveTime>/864000000000)) >= (1073741824*0) && (<Uploaded>/(<ActiveTime>/864000000000)) < (1073741824*0.10) "
    },
    {
        "Name": "ULperDay_0.10GB_0.20GB",
        "Type": "AutoTag",
        "Tag": "ULperDay_0.10GB_0.20GB",
        "Criteria": "(<Uploaded>/(<ActiveTime>/864000000000)) >= (1073741824*0.1) && (<Uploaded>/(<ActiveTime>/864000000000)) < (1073741824*0.20) "
    },
    {
        "Name": "ULperDay_0.20GB_0.30GB",
        "Type": "AutoTag",
        "Tag": "ULperDay_0.20GB_0.30GB",
        "Criteria": "(<Uploaded>/(<ActiveTime>/864000000000)) >= (1073741824*0.20) && (<Uploaded>/(<ActiveTime>/864000000000)) < (1073741824*0.3) "
    },
    {
        "Name": "ULperDay_0.30GB_0.40GB",
        "Type": "AutoTag",
        "Tag": "ULperDay_0.30GB_0.40GB",
        "Criteria": "(<Uploaded>/(<ActiveTime>/864000000000)) >= (1073741824*0.30) && (<Uploaded>/(<ActiveTime>/864000000000)) < (1073741824*0.4) "
    },
    {
        "Name": "ULperDay_0.40GB_0.50GB",
        "Type": "AutoTag",
        "Tag": "ULperDay_0.40GB_0.50GB",
        "Criteria": "(<Uploaded>/(<ActiveTime>/864000000000)) >= (1073741824*0.40) && (<Uploaded>/(<ActiveTime>/864000000000)) < (1073741824*0.5) "
    },
    {
        "Name": "ULperDay_0.50GB_plus",
        "Type": "AutoTag",
        "Tag": "ULperDay_0.50GB_plus",
        "Criteria": "(<Uploaded>/(<ActiveTime>/864000000000)) >= (1073741824*0.5) "
    },
    {
      "Name": "Tag_TvShow_EpisodePattern",
      "Type": "AutoTag",
      "Tag": "tv_show",
      "Criteria": "match(\"<Name>\", \"S[0-9][0-9]E[0-9][0-9]\")"
    },
    {
      "Name": "Tag_Music_ByPath",
      "Type": "AutoTag",
      "Tag": "music",
      "Criteria": "contains(\"<SavePath>\", \"/Music/\")"
    },
    {
      "Name": "Tag_Inactive_30d",
      "Type": "AutoTag",
      "Tag": "inactive_30d",
      "Criteria": "daysAgo(\"<LastActivityTime>\") >= 30.0"
    },
    {
      "Name": "Tag_Inactive_90d",
      "Type": "AutoTag",
      "Tag": "inactive_90d",
      "Criteria": "daysAgo(\"<LastActivityTime>\") >= 90.0"
    },
    {
      "Name": "Tag_OldMovie",
      "Type": "AutoTag",
      "Tag": "old_movie",
      "Criteria": "contains(\"<Category>\", \"Movies\") && daysAgo(\"<AddedOn>\") >= 365.0"
    },
    {
      "Name": "Tag_NoViews_OnPlex",
      "Type": "AutoTag",
      "Tag": "NoViews",
      "Criteria": "(<plex_viewCount> == 0)"
    },
    // ───────────── AutoCategory (from autoCategories) ─────────────
    {
      "Name": "Category_Movies_ByQuality",
      "Type": "AutoCategory",
      "Category": "Movies",
      "Criteria": "match(\"<Name>\", \"(720p|1080p|2160p|BluRay)\")"
    },
    {
      "Name": "Category_TV_ByEpisode",
      "Type": "AutoCategory",
      "Category": "TV",
      "Criteria": "match(\"<Name>\", \"S[0-9][0-9]E[0-9][0-9]\")"
    },
    {
      "Name": "Category_Music_ByPath",
      "Type": "AutoCategory",
      "Category": "Music",
      "Criteria": "contains(\"<SavePath>\", \"/Music/\")"
    },
    {
      "Name": "Category_Software_ByPath",
      "Type": "AutoCategory",
      "Category": "Software",
      "Criteria": "contains(\"<SavePath>\", \"/Software/\")"
    },
    // ───────────── AutoScript (from autoScripts) ─────────────
    {
      "Name": "Script_UnzipDone",
      "Type": "AutoScript",
      "Criteria": "(\"<Progress>\" == \"1\") && ((\"Movies\" == \"<Category>\") || (\"Shows\" == \"<Category>\")) && match(\"<ContentPath>\",\"(Shows|Movies)\")",
      "RunDir": "<ContentPath>",
      "Shebang": "/bin/bash",
      "Script": "unrar x -o- *.rar", //requires unrar to be installed
      "Timeout": 3000
    },
    // ───────────── AutoMove (from autoMoves) ─────────────
    {
      "Name": "Move_ToH00_FromS00_Stale_ShowsMovies",
      "Type": "AutoMove",
      "Path": "/media/jgarza/H00/Torrents/<Category>",
      "Criteria": " (</media/jgarza/H00_PercentUsed> < 0.9 ) && (<ActiveTime>/864000000000 >= 14.0) && ( daysAgo(\"<LastActivityTime>\") >= 3.0) && (daysAgo(\"<LastSeenComplete>\") >= 14.0) && match(\"<Category>\",\"(Shows|Movies)\") && match(\"<SavePath>\",\"S00\") "
    },
    // ───────────── AutoSpeed (from autoSpeeds) ─────────────
    {
      "Name": "Speed_Unlimited_ShowsMovies",
      "Type": "AutoSpeed",
      "UploadSpeed": 0,
      "UownloadSpeed": 0,
      "Criteria": "match(\"<Category>\",\"(Shows|Movies)\")"
    }
  ]
}
```


## Build and run
```
dotnet build
```

## Build for Windows 🪟
```
dotnet publish ./qbt_auto.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o ./bin/win

```

## Build for Linux 🐧
```
dotnet publish ./qbt_auto.csproj -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true -o ./bin/linux

```
