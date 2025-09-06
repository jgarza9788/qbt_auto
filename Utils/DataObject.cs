/*
 * ============================================================================
 *  Project:      qbt_auto
 *  File:         DataObjects.cs
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

namespace Utils
{
    class DataObject
    {
        public string FilePath;
        public Dictionary<string, object> data = new Dictionary<string, object>();

        public DataObject(string FilePath)
        {
            this.FilePath = FilePath;
            Load();
        }

        public void Save()
        {
            var json = Json5.Serialize(data);
            File.WriteAllText(FilePath, json);
        }

        public void Load()
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                data = Json5.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
            }
            else
            {
                Console.WriteLine("File not found: " + FilePath);
            }
        }
    }

}    
