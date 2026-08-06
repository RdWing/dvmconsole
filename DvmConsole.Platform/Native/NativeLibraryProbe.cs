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
    /// One deliberate exception exists: on macOS the three P/Invoke libraries
    /// the audio backend consumes — CoreAudio, AudioToolbox and libSystem —
    /// cannot be resolved by bare logical name, because dyld does not search
    /// framework bundle directories for bare names and libSystem's dylib is
    /// version-suffixed. For exactly those three logical names the probe maps
    /// to the library's install path before loading (see
    /// <see cref="ResolveLoadName(string, bool)"/>). Every other logical name
    /// is still loaded as-is under the OS's own resolution rules, and results
    /// always carry the caller's logical name — never the mapped path.
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
        ** Fields
        */

        private readonly bool _isMacOS;

        /*
        ** Constructors
        */

        /// <summary>
        /// Derives the macOS host check from the runtime
        /// (<see cref="OperatingSystem.IsMacOS"/>), so the install-path
        /// fallback applies exactly on macOS and nowhere else.
        /// </summary>
        public NativeLibraryProbe()
            : this(OperatingSystem.IsMacOS())
        {
        }

        /// <summary>
        /// Uses the supplied host flag, so the macOS install-path fallback can
        /// be controlled explicitly (e.g. from tests on any host).
        /// </summary>
        /// <param name="isMacOS">True when the probe must behave as if
        /// running on macOS.</param>
        public NativeLibraryProbe(bool isMacOS)
        {
            _isMacOS = isMacOS;
        }

        /*
        ** Methods
        */

        /// <summary>
        /// Probes a native library by logical name against the required
        /// exports.
        /// </summary>
        /// <param name="logicalName">Logical library name without any
        /// OS-specific extension, prefix, path or framework bundle name,
        /// e.g. "dvmvocoder".</param>
        /// <param name="requiredExports">Export symbols the library must
        /// provide; at least one is required.</param>
        /// <returns>A success result when the library loads and every required
        /// export is present; otherwise a failure result naming the library
        /// and the missing exports or load failure.</returns>
        /// <exception cref="ArgumentException">When
        /// <paramref name="logicalName"/> is null, empty, whitespace, or an OS
        /// file name or path (contains a path separator, ".dll", ".so",
        /// ".dylib" or ".framework"), or when
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
                    $"'{logicalName}' is an OS file name or path; provide a logical library name without an extension, prefix, path or framework bundle.",
                    nameof(logicalName));
            }

            var loadName = ResolveLoadName(logicalName, _isMacOS);
            if (!NativeLibrary.TryLoad(loadName, out IntPtr handle))
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
        /// True when the logical name carries an OS file name or path, which
        /// the probe contract forbids: a path separator (Unix or Windows), a
        /// platform library extension, or a framework bundle name. Matches
        /// case-insensitively so "LIB.DLL" and "CoreAudio.FRAMEWORK" style
        /// names are rejected too.
        /// </summary>
        private static bool IsOsFileName(string logicalName)
        {
            return logicalName.Contains("/", StringComparison.Ordinal)
                || logicalName.Contains("\\", StringComparison.Ordinal)
                || logicalName.Contains(".dll", StringComparison.OrdinalIgnoreCase)
                || logicalName.Contains(".so", StringComparison.OrdinalIgnoreCase)
                || logicalName.Contains(".dylib", StringComparison.OrdinalIgnoreCase)
                || logicalName.Contains(".framework", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves the name handed to <see cref="NativeLibrary.TryLoad"/> for
        /// a validated logical library name. Off macOS every name passes
        /// through unchanged, so the OS applies its own resolution rules.
        ///
        /// On macOS exactly three logical names — "CoreAudio", "AudioToolbox"
        /// and "libSystem" — map to their install paths, because dyld does not
        /// resolve bare framework names and libSystem's dylib is
        /// version-suffixed. The match is case-insensitive; every other
        /// logical name passes through unchanged. This mapping is the sole
        /// exception to the probe's logical-name contract: it changes only the
        /// load name, never the logical name carried by results or
        /// diagnostics.
        /// </summary>
        /// <param name="logicalName">The logical library name, already
        /// validated by <see cref="Probe(string, IReadOnlyList{string})"/>.
        /// </param>
        /// <param name="isMacOS">True when the host is macOS (the production
        /// probe passes <see cref="OperatingSystem.IsMacOS"/>).</param>
        /// <returns>The load name to hand to the OS loader.</returns>
        public static string ResolveLoadName(string logicalName, bool isMacOS)
        {
            if (!isMacOS)
            {
                return logicalName;
            }

            return logicalName switch
            {
                _ when string.Equals(logicalName, "CoreAudio", StringComparison.OrdinalIgnoreCase)
                    => "/System/Library/Frameworks/CoreAudio.framework/CoreAudio",
                _ when string.Equals(logicalName, "AudioToolbox", StringComparison.OrdinalIgnoreCase)
                    => "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox",
                _ when string.Equals(logicalName, "libSystem", StringComparison.OrdinalIgnoreCase)
                    => "/usr/lib/libSystem.B.dylib",
                _ => logicalName,
            };
        }
    }
}
