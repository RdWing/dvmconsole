// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using dvmconsole;
using DvmConsole.Core.Networking;
using fnecore;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// The real <see cref="IFneTransportFactory"/>: creates a
    /// <see cref="FnecorePeerAdapter"/> per configured system and keeps
    /// a case-insensitive system-name registry of every adapter it has
    /// created. <see cref="ResolveAdapter"/> lets the shell's voice
    /// traffic sender and receive glue find the live adapter for a
    /// system; <see cref="OnCreated"/> lets the shell subscribe the
    /// receive glue to adapters as they are created (the connection
    /// service owns the transports, and a Restart creates a fresh
    /// adapter through this same hook).
    ///
    /// The registry is a <see cref="ConcurrentDictionary{TKey,TValue}"/>:
    /// Create runs on the UI thread and the restart-scheduler thread
    /// pool while ResolveAdapter runs on the audio capture thread
    /// during PTT, so the registry must tolerate concurrent read+write.
    /// </summary>
    public sealed class FnecoreTransportFactory : IFneTransportFactory
    {
        private readonly ConcurrentDictionary<string, FnecorePeerAdapter> adapters =
            new ConcurrentDictionary<string, FnecorePeerAdapter>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Optional creation hook invoked with every adapter the
        /// factory creates, before <see cref="Create"/> returns. The
        /// shell sets this to subscribe the receive glue to each
        /// adapter's frame events (including fresh adapters created by
        /// a service Restart).
        /// </summary>
        public Action<FnecorePeerAdapter>? OnCreated { get; set; }

        /// <summary>
        /// Optional FNE logger callback copied onto every adapter before it
        /// is published through <see cref="OnCreated"/>. The callback is
        /// owned by the shell and may redact secrets at its boundary.
        /// </summary>
        public Action<LogLevel, string>? DiagnosticWriter { get; set; }

        /// <summary>
        /// Detaches the shell-owned logger from this factory and its adapters.
        /// </summary>
        public void ClearDiagnosticWriter()
        {
            DiagnosticWriter = null;
            foreach (FnecorePeerAdapter adapter in adapters.Values)
            {
                adapter.ClearDiagnosticWriter();
            }
        }

        /// <inheritdoc />
        public IFneTransport Create(Codeplug.System system)
        {
            if (system is null)
            {
                throw new ArgumentNullException(nameof(system));
            }

            if (string.IsNullOrWhiteSpace(system.Name))
            {
                throw new ArgumentException("system name", nameof(system));
            }

            var adapter = new FnecorePeerAdapter(system);
            if (DiagnosticWriter is { } diagnosticWriter)
            {
                adapter.SetDiagnosticWriter(diagnosticWriter);
            }

            adapters[system.Name] = adapter;
            OnCreated?.Invoke(adapter);
            return adapter;
        }

        /// <summary>
        /// Resolves the adapter registered for the given system name.
        /// The lookup is case-insensitive (ResourceIdentity.SystemMatches
        /// parity); unknown names resolve to null.
        /// </summary>
        /// <param name="systemName">The configured system name.</param>
        /// <returns>The registered adapter, or null when unknown.</returns>
        public FnecorePeerAdapter? ResolveAdapter(string systemName)
            => !string.IsNullOrEmpty(systemName) && adapters.TryGetValue(systemName, out var adapter)
                ? adapter
                : null;
    }
}
