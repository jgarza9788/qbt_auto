/*
 * ============================================================================
 *  Project:      qbt_auto
 *  File:         DriveInfo.cs
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


using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace Utils
{
    public static class Drives
    {
        public static Dictionary<string, object> getDriveData()
        {
            Dictionary<string, object> DriveData = new Dictionary<string, object>();

            foreach (DriveInfo di in DriveInfo.GetDrives())
            {
                if (OperatingSystem.IsWindows())
                {
                    //windows drives
                }
                else
                {
                    //we don't need all the linux drives
                    if (di.Name.StartsWith("/media/") || di.Name == "/")
                    {
                        //linux drives
                    }
                    else
                    {
                        //skip these
                        continue;
                    }
                }

                DriveData[$"{di.Name}_TotalSizeGB"] = di.TotalSize / (1024 * 1024 * 1024);
                DriveData[$"{di.Name}_FreeSizeGB"] = di.TotalFreeSpace / (1024 * 1024 * 1024);
                DriveData[$"{di.Name}_UsedSizeGB"] = (di.TotalSize - di.TotalFreeSpace) / (1024 * 1024 * 1024);

                float TotalSize = di.TotalSize / (1024 * 1024 * 1024);
                float Used = (di.TotalSize - di.TotalFreeSpace) / (1024 * 1024 * 1024);

                DriveData[$"{di.Name}_PercentUsed"] = (float)( Used / TotalSize);

                // Console.WriteLine($"{di.Name}_TotalSizeGB  {DriveData[$"{di.Name}_TotalSizeGB"]}");
                // Console.WriteLine($"{di.Name}_FreeSizeGB   {DriveData[$"{di.Name}_FreeSizeGB"]}");
                // Console.WriteLine($"{di.Name}_UsedSizeGB   {DriveData[$"{di.Name}_UsedSizeGB"]}");
                // Console.WriteLine($"{di.Name}_PercentUsed  {DriveData[$"{di.Name}_PercentUsed"]}");


            }

            return DriveData;
        }
    } 
}
