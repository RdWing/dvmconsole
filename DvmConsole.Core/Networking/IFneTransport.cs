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
using dvmconsole;

namespace DvmConsole.Core.Networking
{
    /// <summary>
    /// Transport seam for one FNE system connection. Mirrors the
    /// observable surface of <c>fnecore.FnePeer</c>: <see cref="Connect"/>
    /// performs the login handshake and raises <see cref="PeerConnected"/>
    /// on success, <see cref="Disconnect"/> tears the connection down,
    /// and the maintenance ping loop (fnecore parity PingTime=5) is
    /// exposed as <see cref="Ping"/> with <see cref="PingAcknowledged"/>
    /// raised when the peer acknowledges the keepalive.
    /// <see cref="PeerDisconnected"/> is raised on connection-state loss.
    /// </summary>
    public interface IFneTransport : IDisposable
    {
        /// <summary>
        /// Connects to the FNE and performs the login handshake. Raises
        /// <see cref="PeerConnected"/> once the peer is online.
        /// </summary>
        void Connect();

        /// <summary>
        /// Disconnects from the FNE. Safe to call when not connected.
        /// </summary>
        void Disconnect();

        /// <summary>
        /// Releases the transport's resources. Redeclared from
        /// <see cref="IDisposable"/> so the transport surface is
        /// self-contained.
        /// </summary>
        new void Dispose();

        /// <summary>
        /// Sends one maintenance keepalive. <see cref="PingAcknowledged"/>
        /// is raised when the peer acknowledges it.
        /// </summary>
        void Ping();

        /// <summary>
        /// Raised when the login handshake completes and the peer is
        /// connected.
        /// </summary>
        event Action? PeerConnected;

        /// <summary>
        /// Raised when the connection state is lost.
        /// </summary>
        event Action? PeerDisconnected;

        /// <summary>
        /// Raised when the peer acknowledges a keepalive ping.
        /// </summary>
        event Action? PingAcknowledged;
    }

    /// <summary>
    /// Creates <see cref="IFneTransport"/> instances for configured
    /// <see cref="Codeplug.System"/> entries. The real fnecore-backed
    /// adapter is a later slice; until then the shell composes the
    /// unavailable fallback and the service is never asked to create a
    /// transport (zero configured systems).
    /// </summary>
    public interface IFneTransportFactory
    {
        /// <summary>
        /// Creates a transport for the given system configuration.
        /// </summary>
        /// <param name="system">The system configuration to connect to.</param>
        /// <returns>A new, unconnected transport.</returns>
        IFneTransport Create(Codeplug.System system);
    }
}
