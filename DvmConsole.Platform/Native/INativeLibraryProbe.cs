// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Collections.Generic;

namespace DvmConsole.Platform.Native
{
    /// <summary>
    /// Result of probing a native library by logical name.
    /// </summary>
    public sealed class NativeLibraryProbeResult
    {
        private NativeLibraryProbeResult(string logicalName, bool isSuccess, string? diagnostic)
        {
            LogicalName = logicalName;
            IsSuccess = isSuccess;
            Diagnostic = diagnostic;
        }

        /// <summary>The logical library name that was probed.</summary>
        public string LogicalName { get; }

        /// <summary>True when every required export was found.</summary>
        public bool IsSuccess { get; }

        /// <summary>Diagnostic message on failure, otherwise null.</summary>
        public string? Diagnostic { get; }

        /// <summary>A successful probe result carrying the logical name and no diagnostic.</summary>
        public static NativeLibraryProbeResult Success(string logicalName)
            => new(logicalName, true, null);

        /// <summary>A failed probe result carrying the logical name and a diagnostic.</summary>
        public static NativeLibraryProbeResult Failure(string logicalName, string diagnostic)
            => new(logicalName, false, diagnostic);
    }

    /// <summary>
    /// Probes whether a native library is loadable on the host by logical name
    /// (e.g. "dvmvocoder"), never by OS file name. The probe reports required
    /// export symbols so failures name what is missing.
    /// </summary>
    public interface INativeLibraryProbe : IAsyncDisposable
    {
        /// <summary>
        /// Probes a native library.
        /// </summary>
        /// <param name="logicalName">Logical library name without any OS-specific
        /// extension or prefix, e.g. "dvmvocoder".</param>
        /// <param name="requiredExports">Export symbols the library must provide;
        /// at least one is required.</param>
        /// <returns>A success or failure result.</returns>
        NativeLibraryProbeResult Probe(string logicalName, IReadOnlyList<string> requiredExports);
    }
}
