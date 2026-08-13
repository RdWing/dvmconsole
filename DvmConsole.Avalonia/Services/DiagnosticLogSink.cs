// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using dvmconsole;
using fnecore;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Thread-safe frontend diagnostic sink over the Core-owned recent-line
    /// buffer. The sink owns neither the buffer nor any UI resources.
    /// </summary>
    public sealed class DiagnosticLogSink : IDisposable
    {
        private readonly object sync = new object();
        private readonly LogBuffer buffer;
        private readonly List<string> sensitiveValues = new();
        private bool disposed;

        /// <summary>
        /// Creates a sink over the given shared buffer. Non-empty sensitive
        /// values are replaced before a line reaches the buffer.
        /// </summary>
        public DiagnosticLogSink(
            LogBuffer buffer,
            IEnumerable<string>? sensitiveValues = null)
        {
            this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            AddSensitiveValues(sensitiveValues);
        }

        /// <summary>The Core buffer shared with viewers over this sink.</summary>
        public LogBuffer Buffer => buffer;

        /// <summary>
        /// Adds values that must never reach the shared buffer. This supports
        /// codeplug replacement without rebuilding the app-lifetime sink.
        /// </summary>
        public void AddSensitiveValues(IEnumerable<string>? values)
        {
            if (values is null)
                return;

            lock (sync)
            {
                foreach (string value in values.Where(value => !string.IsNullOrEmpty(value)))
                {
                    if (!this.sensitiveValues.Contains(value, StringComparer.Ordinal))
                    {
                        this.sensitiveValues.Add(value);
                    }
                }

                this.sensitiveValues.Sort((left, right) => right.Length.CompareTo(left.Length));
            }
        }

        /// <summary>
        /// Writes an already-rendered diagnostic line unless this sink has
        /// been disposed.
        /// </summary>
        public void Write(string line)
        {
            if (line is null)
                throw new ArgumentNullException(nameof(line));

            string redactedLine;
            lock (sync)
            {
                if (disposed)
                    return;

                redactedLine = Redact(line);
            }

            buffer.WriteLine(redactedLine);
        }

        /// <summary>
        /// Writes an FNE diagnostic with its level prefix preserved.
        /// </summary>
        public void Write(LogLevel level, string message)
        {
            if (message is null)
                throw new ArgumentNullException(nameof(message));

            Write($"{level} {message}");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                sensitiveValues.Clear();
            }
        }

        private string Redact(string line)
        {
            foreach (string sensitiveValue in sensitiveValues)
            {
                line = line.Replace(sensitiveValue, "[REDACTED]", StringComparison.Ordinal);
            }

            return line;
        }
    }
}
