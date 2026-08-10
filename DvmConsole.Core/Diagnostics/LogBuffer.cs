// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace dvmconsole
{
    /// <summary>
    /// Headless recent-log ring buffer matching the WPF Log recent-lines contract
    /// (dvmconsole/Log.cs). Holds only already-rendered lines; timestamp formatting,
    /// file/trace/console sinks, static global state, and dispatcher/UI remain
    /// frontend seams.
    /// </summary>
    public sealed class LogBuffer
    {
        private const int MaxRecentLines = 500;

        private readonly object sync = new object();
        private readonly Queue<string> recentLines = new Queue<string>();

        /// <summary>
        /// Maximum number of recently rendered lines retained. Matches the WPF
        /// MAX_RECENT_LOG_LINES value.
        /// </summary>
        public int Capacity => MaxRecentLines;

        /// <summary>
        /// Raised after a line has been enqueued and is visible to
        /// <see cref="GetRecentLines"/>. Subscribers are invoked outside the
        /// buffer lock, mirroring WPF Log.AddRecentLine ordering.
        /// </summary>
        public event Action<string>? LogLineWritten;

        /// <summary>
        /// Appends an already-rendered line, evicting the oldest lines once
        /// capacity is reached.
        /// </summary>
        /// <param name="line">Rendered log line to retain.</param>
        public void WriteLine(string line)
        {
            lock (sync)
            {
                while (recentLines.Count >= MaxRecentLines)
                    recentLines.Dequeue();

                recentLines.Enqueue(line);
            }

            LogLineWritten?.Invoke(line);
        }

        /// <summary>
        /// Returns a snapshot of the most recently rendered log lines, oldest
        /// first. The returned list is independent of subsequent writes.
        /// </summary>
        public IReadOnlyList<string> GetRecentLines()
        {
            lock (sync)
                return recentLines.ToList();
        }
    }
} // namespace dvmconsole
