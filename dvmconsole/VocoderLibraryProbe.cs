// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2025 Caleb, K4PHP
*   Copyright (C) 2025 Bryan Biedenkapp, N2PLL
*
*/

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace dvmconsole
{
    /// <summary>
    /// Platform-neutral startup probe for the native dvmvocoder `libvocoder`
    /// library. Loads by the logical library name through
    /// <see cref="NativeLibrary"/> so the OS applies its own resolution rules
    /// (libvocoder.so on Linux, libvocoder.dylib on macOS, libvocoder.dll on
    /// Windows) instead of a hard-coded platform filename, then validates that
    /// every required C ABI export the managed wrappers call is present.
    ///
    /// A probe is intentionally side-effect free beyond the transient load:
    /// successful handles are always freed, including when an export is
    /// missing, so the startup check never leaks the library into the process.
    /// </summary>
    internal static class VocoderLibraryProbe
    {
        /// <summary>
        /// Logical native library name used by the DllImports in
        /// VocoderInterop.cs.
        /// </summary>
        public const string LogicalLibraryName = "libvocoder";

        /// <summary>
        /// Upstream project page referenced by every diagnostic.
        /// </summary>
        public const string GuidanceUrl = "https://github.com/DVMProject/dvmvocoder";

        /// <summary>
        /// The eight DVMProject/dvmvocoder C ABI exports the console requires:
        /// encoder and decoder create/encode/decode(./bits)/delete.
        /// </summary>
        public static readonly string[] RequiredExports =
        {
            "MBEEncoder_Create",
            "MBEEncoder_Encode",
            "MBEEncoder_EncodeBits",
            "MBEEncoder_Delete",
            "MBEDecoder_Create",
            "MBEDecoder_Decode",
            "MBEDecoder_DecodeBits",
            "MBEDecoder_Delete",
        };

        /*
        ** Methods
        */

        /// <summary>
        /// Probes the logical <see cref="LogicalLibraryName"/> library against
        /// <see cref="RequiredExports"/>.
        /// </summary>
        /// <returns>Null when the library loads and every required export is
        /// present; otherwise a diagnostic naming the library and the missing
        /// exports or load failure, with upstream guidance.</returns>
        public static string Probe()
        {
            return Probe(LogicalLibraryName, RequiredExports);
        }

        /// <summary>
        /// Probes an explicitly named native library against an explicit
        /// required-export list. Test hook for exercising the load-failure and
        /// missing-export diagnostic paths without touching the real library.
        /// </summary>
        /// <param name="libraryName">Logical library name (or absolute path).</param>
        /// <param name="requiredExports">Symbols that must resolve; defaults to
        /// <see cref="RequiredExports"/> when null.</param>
        /// <returns>Null when the library loads and every required export is
        /// present; otherwise a diagnostic naming the library and the missing
        /// exports or load failure, with upstream guidance.</returns>
        /// <exception cref="ArgumentException">When <paramref name="libraryName"/>
        /// is null, empty or whitespace.</exception>
        public static string Probe(string libraryName, string[] requiredExports = null)
        {
            if (string.IsNullOrWhiteSpace(libraryName))
                throw new ArgumentException("A native library name is required.", nameof(libraryName));

            string[] exports = requiredExports ?? RequiredExports;

            if (!NativeLibrary.TryLoad(libraryName, out IntPtr handle))
            {
                return $"The {libraryName} native library could not be loaded. It is required for operation of the console, please see: {GuidanceUrl}.";
            }

            try
            {
                List<string> missing = null;
                foreach (string export in exports)
                {
                    if (!NativeLibrary.TryGetExport(handle, export, out _))
                    {
                        if (missing == null)
                            missing = new List<string>();
                        missing.Add(export);
                    }
                }

                if (missing != null)
                {
                    return $"The {libraryName} native library is missing required export(s): {string.Join(", ", missing)}. It is required for operation of the console, please see: {GuidanceUrl}.";
                }

                return null;
            }
            finally
            {
                // Always release the handle, including the missing-export
                // failure path: the probe must never leak the library into the
                // process after it has served its diagnostic purpose.
                NativeLibrary.Free(handle);
            }
        }
    } // internal static class VocoderLibraryProbe
} // namespace dvmconsole
