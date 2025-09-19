/*
 * ============================================================================
 *  Project:      qbt_auto
 *  File:         Program.cs
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

using System;
using Utils;
using NLog;

namespace QbtAuto
{
    class Program
    {
        private static NLog.Logger loggerFC = NLog.LogManager.GetLogger("LoggerFC");

        // Entry point of the application
        static async Task Main(string[] args)
        {

            try
            {
                QbtAuto qbtauto = new QbtAuto(args);
                await qbtauto.RunAllAutosAsync();
            }
            catch (Exception ex)
            {
                loggerFC.Error(ex);
            }
        }
    }
}
