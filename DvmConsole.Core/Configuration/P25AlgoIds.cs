// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2024-2025 Caleb, K4PHP
*   Copyright (C) 2025 Bryan Biedenkapp, N2PLL
*   Copyright (C) 2025 Steven Jennison, KD8RHO
*   Copyright (C) 2025 Lorenzo L Romero, K2LLR
*   Copyright (C) 2026 C. Lovell, Dev_Ranger
*
*/

namespace dvmconsole
{
    /// <summary>
    /// P25 encryption algorithm identifiers used by the codeplug channel
    /// schema. Values are the verified literals from fnecore/P25/P25Defines.cs
    /// and are duplicated here so the portable DvmConsole.Core assembly has no
    /// dependency on fnecore. They are part of the on-disk codeplug contract
    /// and must never be renumbered.
    /// </summary>
    public static class P25AlgoIds
    {
        /// <summary>
        /// No encryption.
        /// </summary>
        public const byte P25_ALGO_UNENCRYPT = 0x80;
        /// <summary>
        /// DES-OFB encryption.
        /// </summary>
        public const byte P25_ALGO_DES = 0x81;
        /// <summary>
        /// AES-256 encryption.
        /// </summary>
        public const byte P25_ALGO_AES = 0x84;
        /// <summary>
        /// ARC4 encryption.
        /// </summary>
        public const byte P25_ALGO_ARC4 = 0xAA;
    } // public static class P25AlgoIds
} // namespace dvmconsole
