/*
* ============================================================================
*  Project:      qbt_auto
*  File:         ProgressRenderer.cs
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
    class ProgressRenderer : IDisposable
    {
        private readonly int _total;
        private readonly Func<int> _completedProvider;
        private readonly Timer _timer;
        private readonly bool _interactive;
        private readonly object _renderLock = new();
        private readonly NLog.Logger _log = NLog.LogManager.GetLogger("LoggerC");

        public ProgressRenderer(int total, Func<int> completedProvider, int intervalMs = 200)
        {
            _total = Math.Max(1, total);
            _completedProvider = completedProvider;

            // Detect interactive console (very conservative)
            _interactive =
                !Console.IsOutputRedirected &&
                !Console.IsErrorRedirected;

            _timer = new Timer(_ => Render(), null, intervalMs, intervalMs);
        }

        private void Render()
        {
            lock (_renderLock)
            {
                int completed = Math.Min(_completedProvider(), _total);
                double percent = (double)completed / _total;
                const int barSize = 40;
                int filled = (int)(percent * barSize);
                if (filled < 0) filled = 0;
                if (filled > barSize) filled = barSize;

                string bar = new string('#', filled).PadRight(barSize);

                if (_interactive)
                {
                    // Live in-place bar on a real TTY
                    Console.Write($"\r[{bar}] {percent:P2}   ");
                    Console.Out.Flush();
                }
                else
                {
                    // Non-interactive (logs, services, redirected output):
                    // log a periodic snapshot; throttle via the timer interval
                    _log.Info($"[{bar}] {percent:P2}");
                }
            }
        }

        public void Complete()
        {
            Render();
            if (_interactive)
            {
                Console.WriteLine(); // finish on a new line
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
