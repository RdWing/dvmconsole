// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Native
{
    /// <summary>
    /// BCL-only <see cref="INativeLibraryProbe"/> implementation. Probes a
    /// native library by logical name (e.g. "dvmvocoder"), never by OS file
    /// name, so the OS applies its own resolution rules (libc.so / libc.dylib
    /// / libc.dll and friends) through <see cref="NativeLibrary"/> instead of a
    /// hard-coded platform filename. The probe then validates that every
    /// required C ABI export the caller consumes is present.
    ///
    /// A probe is intentionally side-effect free beyond the transient load:
    /// successful handles are always freed, including when an export is
    /// missing, so a probe never leaks the library into the process. The
    /// probe holds no state, so <see cref="DisposeAsync"/> is a no-op that may
    /// be called any number of times without affecting later probes.
    /// </summary>
    public sealed class NativeLibraryProbe : INativeLibraryProbe
    {
        /*
        ** Methods
        */

        /// <summary>
        /// Probes a native library by logical name against the required
        /// exports.
        /// </summary>
        /// <param name="logicalName">Logical library name without any
        /// OS-specific extension or prefix, e.g. "dvmvocoder".</param>
        /// <param name="requiredExports">Export symbols the library must
        /// provide; at least one is required.</param>
        /// <returns>A success result when the library loads and every required
        /// export is present; otherwise a failure result naming the library
        /// and the missing exports or load failure.</returns>
        /// <exception cref="ArgumentException">When
        /// <paramref name="logicalName"/> is null, empty, whitespace, or an OS
        /// file name (contains ".dll", ".so" or ".dylib"), or when
        /// <paramref name="requiredExports"/> is empty.</exception>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="requiredExports"/> is null.</exception>
        public NativeLibraryProbeResult Probe(string logicalName, IReadOnlyList<string> requiredExports)
        {
            if (string.IsNullOrWhiteSpace(logicalName))
                throw new ArgumentException("A logical native library name is required.", nameof(logicalName));

            if (requiredExports == null)
                throw new ArgumentNullException(nameof(requiredExports));

            if (requiredExports.Count == 0)
                throw new ArgumentException("At least one required export must be declared.", nameof(requiredExports));

            if (IsOsFileName(logicalName))
            {
                throw new ArgumentException(
                    $"'{logicalName}' is an OS file name; provide a logical library name without an extension or prefix.",
                    nameof(logicalName));
            }

            if (!NativeLibrary.TryLoad(logicalName, out IntPtr handle))
            {
                return NativeLibraryProbeResult.Failure(
                    logicalName,
                    $"The {logicalName} native library could not be loaded by logical name.");
            }

            try
            {
                List<string>? missing = null;
                foreach (string export in requiredExports)
                {
                    if (!NativeLibrary.TryGetExport(handle, export, out _))
                    {
                        (missing ??= new List<string>()).Add(export);
                    }
                }

                if (missing != null)
                {
                    return NativeLibraryProbeResult.Failure(
                        logicalName,
                        $"The {logicalName} native library is missing required export(s): {string.Join(", ", missing)}.");
                }

                return NativeLibraryProbeResult.Success(logicalName);
            }
            finally
            {
                // Always release the handle, including the missing-export
                // failure path: the probe must never leak the library into the
                // process after it has served its diagnostic purpose.
                NativeLibrary.Free(handle);
            }
        }

        /// <summary>
        /// No-op: a probe holds no resources, is stateless, and remains fully
        /// functional after disposal. Safe to call any number of times.
        /// </summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /*
        ** Helpers
        */

        /// <summary>
        /// True when the logical name carries a platform library extension,
        /// which the probe contract forbids. Matches case-insensitively so
        /// "LIB.DLL" style names are rejected too.
        /// </summary>
        private static bool IsOsFileName(string logicalName)
        {
            return logicalName.Contains(".dll", StringComparison.OrdinalIgnoreCase)
                || logicalName.Contains(".so", StringComparison.OrdinalIgnoreCase)
                || logicalName.Contains(".dylib", StringComparison.OrdinalIgnoreCase);
        }
    }
}
