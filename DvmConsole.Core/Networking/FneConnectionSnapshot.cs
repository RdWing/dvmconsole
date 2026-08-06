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
namespace DvmConsole.Core.Networking
{
    /// <summary>
    /// Immutable snapshot of one FNE system connection's state. This is
    /// the Core copy of the WPF <c>dvmconsole.FneConnectionSnapshot</c>
    /// DTO (dvmconsole/MainWindow.FneConnections.cs) minus the derived
    /// <c>StatusText</c>, so platform-neutral consumers can observe
    /// connection state without touching UI concerns.
    /// </summary>
    /// <param name="SystemName">The canonical system name.</param>
    /// <param name="IsConnected">True when the FNE peer is connected.</param>
    /// <param name="IsBusy">True while a start/stop/restart is in flight.</param>
    /// <param name="IsStarted">True once the transport was started.</param>
    public sealed record FneConnectionSnapshot(
        string SystemName,
        bool IsConnected,
        bool IsBusy,
        bool IsStarted);
}
