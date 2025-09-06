/*
 * ============================================================================
 *  Project:      qbt_auto
 *  File:         Cmd.cs
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

using System.Diagnostics;

namespace Utils
{
    public static class Cmd
    {
        public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
            string command,
            string[] args,
            string? workingDir = null,
            TimeSpan? timeout = null,
            CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout is not null) cts.CancelAfter(timeout.Value);

            var psi = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (workingDir is not null) psi.WorkingDirectory = workingDir;

            // ArgumentList avoids quoting issues
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi };

            p.Start();

            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);

            await p.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return (p.ExitCode, stdout, stderr);
        }

        public static async Task<(int ExitCode, string StdOut, string StdErr)> SheBangCmdAsync(string shebang, string command, string? workingDir = null, int timeoutSeconds = 3000)
        {
            string c = "/c";

            if (!OperatingSystem.IsWindows())
            {
                c = "-lc";
            }
            
            return await RunAsync(shebang, new[] { c, command }, workingDir, TimeSpan.FromSeconds(timeoutSeconds));
        }

    }
}
