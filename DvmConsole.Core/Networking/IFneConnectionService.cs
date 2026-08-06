// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2024 Caleb, K4PHP
*   Copyright (C) 2025 Steven Jennison, KD8RHO
*   Copyright (C) 2026 C. Lovell, Dev_Ranger
*
*/
#nullable enable
using System;

namespace DvmConsole.Core.Networking
{
    /// <summary>
    /// Headless, platform-neutral FNE connection service. Owns the
    /// lifecycle of one transport per configured system (start, stop,
    /// restart, keepalive heartbeat) and publishes an immutable
    /// <see cref="FneConnectionSnapshot"/> through
    /// <see cref="StateChanged"/> on every observable transition. The
    /// service is BCL-only: it never touches fnecore, sockets, audio, a
    /// dispatcher, or any secret material, and it may be driven from any
    /// thread.
    /// </summary>
    public interface IFneConnectionService : IDisposable
    {
        /// <summary>
        /// Starts the connection for the named system. Unknown names and
        /// already-busy rows are a no-op. The row becomes busy and a
        /// transport is created and connected; <see cref="StateChanged"/>
        /// carries the busy snapshot immediately and the connected
        /// snapshot when the transport raises PeerConnected.
        /// </summary>
        /// <param name="systemName">The system name (case-insensitive).</param>
        void Start(string systemName);

        /// <summary>
        /// Stops the connection for the named system. Unknown, busy, or
        /// not-started rows are a no-op.
        /// </summary>
        /// <param name="systemName">The system name (case-insensitive).</param>
        void Stop(string systemName);

        /// <summary>
        /// Releases the service's resources: every system is torn down
        /// and no further <see cref="StateChanged"/> is published.
        /// Redeclared from <see cref="IDisposable"/> so the service
        /// surface is self-contained.
        /// </summary>
        new void Dispose();

        /// <summary>
        /// Restarts the connection for the named system: the current
        /// transport is disconnected and disposed, busy is held across a
        /// short delay, then a fresh transport is created and connected.
        /// Unknown or busy rows are a no-op.
        /// </summary>
        /// <param name="systemName">The system name (case-insensitive).</param>
        void Restart(string systemName);

        /// <summary>
        /// Returns the current snapshot for the named system, or null
        /// for unknown names.
        /// </summary>
        /// <param name="systemName">The system name (case-insensitive).</param>
        /// <returns>The current snapshot, or null when unknown.</returns>
        FneConnectionSnapshot? GetSnapshot(string systemName);

        /// <summary>
        /// Raised on every transition where any of IsConnected, IsBusy,
        /// or IsStarted changes, with the full snapshot. May be raised
        /// from the transport's own thread.
        /// </summary>
        event Action<FneConnectionSnapshot>? StateChanged;
    }
}
