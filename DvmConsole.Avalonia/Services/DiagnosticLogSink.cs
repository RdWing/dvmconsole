// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private static readonly object fileSync = new object();
        private readonly object sync = new object();
        private readonly LogBuffer buffer;
        private readonly List<string> sensitiveValues = new();
        private readonly StreamWriter? fileWriter;
        private bool disposed;

        /// <summary>
        /// Creates a sink over the given shared buffer. Non-empty sensitive
        /// values are replaced before a line reaches the buffer or file. When
        /// <paramref name="filePath"/> is supplied, the sink also appends
        /// timestamped records to that path; an unavailable file sink never
        /// disables the in-memory diagnostic viewer.
        /// </summary>
        public DiagnosticLogSink(
            LogBuffer buffer,
            IEnumerable<string>? sensitiveValues = null,
            string? filePath = null)
        {
            this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            AddSensitiveValues(sensitiveValues);

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                try
                {
                    string? directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    fileWriter = new StreamWriter(
                        new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
                }
                catch (Exception exception)
                {
                    Trace.WriteLine($"Diagnostic log file unavailable: {exception}");
                }
            }
        }

        /// <summary>The Core buffer shared with viewers over this sink.</summary>
        public LogBuffer Buffer => buffer;

        /// <summary>
        /// Adds values that must never reach the shared buffer.
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
        /// Replaces the codeplug-derived sensitive values used for redaction.
        /// This keeps an app-lifetime sink safe across codeplug reloads
        /// without retaining every previously loaded secret forever.
        /// </summary>
        public void ReplaceSensitiveValues(IEnumerable<string>? values)
        {
            lock (sync)
            {
                sensitiveValues.Clear();

                if (values is null)
                    return;

                foreach (string value in values.Where(value => !string.IsNullOrEmpty(value)))
                {
                    if (!sensitiveValues.Contains(value, StringComparer.Ordinal))
                        sensitiveValues.Add(value);
                }

                sensitiveValues.Sort((left, right) => right.Length.CompareTo(left.Length));
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

            WriteRendered(line, "APP");
        }

        /// <summary>
        /// Writes an FNE diagnostic with its level prefix preserved.
        /// </summary>
        public void Write(LogLevel level, string message)
        {
            if (message is null)
                throw new ArgumentNullException(nameof(message));

            WriteRendered($"{level} {message}", "FNE");
        }

        /// <summary>
        /// Writes an application-owned diagnostic with an explicit level.
        /// </summary>
        public void WriteApplication(LogLevel level, string message)
        {
            if (message is null)
                throw new ArgumentNullException(nameof(message));

            WriteRendered($"{level} {message}", "APP");
        }

        /// <summary>
        /// Writes the full managed exception representation after redaction.
        /// </summary>
        public void WriteException(LogLevel level, string context, Exception exception)
        {
            if (context is null)
                throw new ArgumentNullException(nameof(context));
            if (exception is null)
                throw new ArgumentNullException(nameof(exception));

            WriteApplication(level, $"{context}: {exception}");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            StreamWriter? writer;
            lock (sync)
            {
                if (disposed)
                    return;

                disposed = true;
                sensitiveValues.Clear();
                writer = fileWriter;
            }

            if (writer is not null)
            {
                try
                {
                    lock (fileSync)
                        writer.Dispose();
                }
                catch (Exception exception)
                {
                    Trace.WriteLine($"Diagnostic log close failed: {exception}");
                }
            }
        }

        private void WriteRendered(string line, string source)
        {
            string redactedLine;
            lock (sync)
            {
                if (disposed)
                    return;

                redactedLine = Redact(line);
            }

            try
            {
                buffer.WriteLine(redactedLine);
            }
            catch (Exception exception)
            {
                // Diagnostics must never become the process failure path.
                Trace.WriteLine($"Diagnostic viewer publication failed: {exception}");
            }

            if (fileWriter is not null)
            {
                try
                {
                    lock (fileSync)
                    {
                        fileWriter.WriteLine(
                            $"{DateTimeOffset.UtcNow:O} [{source}] {redactedLine}");
                        fileWriter.Flush();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // A concurrent runtime teardown may close the file sink.
                }
                catch (Exception exception)
                {
                    Trace.WriteLine($"Diagnostic log write failed: {exception}");
                }
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
