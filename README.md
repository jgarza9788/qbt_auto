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

![Release](https://img.shields.io/github/v/release/jgarza9788/qbt_auto?include_prereleases&label=latest)
![License](https://img.shields.io/badge/license-FSL-blue)

![Downloads](https://img.shields.io/github/downloads/jgarza9788/qbt_auto/total?label=downloads)
![Last Commit](https://img.shields.io/github/last-commit/jgarza9788/qbt_auto)
![Build](https://img.shields.io/github/actions/workflow/status/jgarza9788/qbt_auto/ci.yml?label=build)
![Tests](https://github.com/jgarza9788/qbt_auto/actions/workflows/tests.yml/badge.svg)


![C#](https://img.shields.io/badge/language-C%23-178600?logo=csharp)
![.NET](https://img.shields.io/badge/.NET-9.0-blue?logo=dotnet)
![qBittorrent](https://img.shields.io/badge/qBittorrent-automation-3D9AE8?logo=qbittorrent&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Linux%20%7C%20Windows-white)

![Stars](https://img.shields.io/github/stars/jgarza9788/qbt_auto?style=social)
![Forks](https://img.shields.io/github/forks/jgarza9788/qbt_auto?style=social)
![Contributors](https://img.shields.io/github/contributors/jgarza9788/qbt_auto)
![Open Issues](https://img.shields.io/github/issues/jgarza9788/qbt_auto)


---

This is a qBittorrent automation tool.
It connects to a qBittorrent instance, loads a config file (config.json by default), and then automatically applies tags and categories to torrents based on rules you define in that config.



## Requirements

* Server running qbttorrent, and access to it.

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

### RoadMap (features that i'll add soon) 
* seed management 
  * Seed - Radio Limit
    * global
    * no limit
    * ratio , total min, inactive min
  * Seed - Speed Limit 
* plex support (maybe)


## Build and run
```
dotnet build "http://###.###.#.###:####" "UserName" "Password" "config.json"
```

## Build for Windows 🪟
```
dotnet publish ./qbt_auto.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o ./bin/win

```

## Build for Linux 🐧
```
dotnet publish ./qbt_auto.csproj -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true -o ./bin/linux

```

## Run 
```
/path/to/qbt_auto -h http://192.168.1.250:8080 -u jgarza9788@gmail.com -p 3832Langley -c config.json
```
```
# if the connection data is in the config file
/path/to/qbt_auto -c config.json
```
### inputs
* -h -host -url
* -u -user 
* -p -password -pwd
* -c -config -configpath
* -v -verbose

## Run at intervals
Run at intervals by adding the command to CRON (linux), or Windows Task Scheduler.

## config.json (example)
```json
{
  //optional - provide connection data in config
  "qbt":{
    "host": "http://###.###.#.###:####",
    "user": "?????",
    "pwd":  "*****"
  },

  "autoTags": [
    {
      // Tag torrents smaller than 1 GB
      "tag": "small_file",
      "criteria": "(<Size> < 1073741824)" 
    },
    {
      // Tag torrents between 1 GB and 10 GB
      "tag": "medium_file",
      "criteria": "(<Size> >= 1073741824) && (<Size> < 10737418240)"
    },
    {
      // Tag torrents larger than 10 GB
      "tag": "large_file",
      "criteria": "(<Size> >= 10737418240)"
    },
    {
      // Tag torrents that look like TV episodes (SxxExx pattern)
      "tag": "tv_show",
      "criteria": "match(\"<Name>\", \"S[0-9][0-9]E[0-9][0-9]\")"
    },
    {
      // Tag torrents saved in the Music folder
      "tag": "music",
      "criteria": "contains(\"<SavePath>\", \"/Music/\")"
    },
    {
      // Tag torrents that had no activity for at least 30 days
      "tag": "inactive_30d",
      "criteria": "daysAgo(\"<LastActivityTime>\") >= 30.0"
    },
    {
      // Tag torrents that had no activity for at least 90 days
      "tag": "inactive_90d",
      "criteria": "daysAgo(\"<LastActivityTime>\") >= 90.0"
    },
    {
      // Tag torrents in category "Movies" that were added at least 1 year ago
      "tag": "old_movie",
      "criteria": "contains(\"<Category>\", \"Movies\") && daysAgo(\"<AddedOn>\") >= 365.0"
    }
  ],

  "autoCategories": [
    {
      // Categorize as Movies if torrent name contains typical quality markers
      "category": "Movies",
      "criteria": "match(\"<Name>\", \"(720p|1080p|2160p|BluRay)\")"
    },
    {
      // Categorize as TV if torrent name has TV episode pattern
      "category": "TV",
      "criteria": "match(\"<Name>\", \"S[0-9][0-9]E[0-9][0-9]\")"
    },
    {
      // Categorize as Music if save path contains /Music/
      "category": "Music",
      "criteria": "contains(\"<SavePath>\", \"/Music/\")"
    },
    {
      // Categorize as Software if save path contains /Software/
      "category": "Software",
      "criteria": "contains(\"<SavePath>\", \"/Software/\")"
    }
  ],

  "autoScripts":[
    {
      //a blank file named unzip.done will be placed to mark this is script was ran.
      "name": "unzip.done",
      "criteria": "(\"<Progress>\" == \"1\") && ((\"Movies\" == \"<Category>\") || (\"Shows\" == \"<Category>\")) && match(\"<ContentPath>\",\"(Shows|Movies)\")",
      "directory": "<ContentPath>",
      //shebang should be ...
      /*
      /bin/bash (for linux)
      /usr/bin/bash (for the user's custom's bash)
      cmd.exe (for windows)
      pwsh (if you use powershell)
      ...or a custom
      */
      "shebang": "/bin/bash", 
      "script": "unrar x -o- *.rar", //this would require unrar to be installed
      "timeout": 3000 //sec => 5hours
    },
  ],

  //these will move a torrent if the criteria is met
  "autoMoves":[
    {
      //<Category> will be replaced with the category from that torrent
      "path": "/media/jgarza/H00/Torrents/<Category>",
      // drive is less than 0.9 (90% full), active time is over 14 days, it's last active time is over 3 days ago, it was completed over 14, the category is Shows or Movies, and it's save path has S00 in it.
      "criteria": " (</media/jgarza/H00_PercentUsed> < 0.9 ) && (<ActiveTime>/864000000000 >= 14.0) && ( daysAgo(\"<LastActivityTime>\") >= 3.0) && (daysAgo(\"<LastSeenComplete>\") >= 14.0) && match(\"<Category>\",\"(Shows|Movies)\") && match(\"<SavePath>\",\"S00\") "
    },
    ]
}
```
