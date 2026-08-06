// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;

namespace DvmConsole.Platform.Native
{
    /// <summary>
    /// Read-only outcome of the startup vocoder-readiness check.
    /// </summary>
    public sealed class VocoderReadinessResult
    {
        /// <summary>
        /// Creates a readiness result.
        /// </summary>
        /// <param name="isReady">True when the vocoder library is ready.</param>
        /// <param name="logicalLibraryName">The logical library name that
        /// was probed.</param>
        /// <param name="diagnostic">Diagnostic message on failure, otherwise
        /// null.</param>
        public VocoderReadinessResult(
            bool isReady,
            string logicalLibraryName,
            string? diagnostic)
        {
            IsReady = isReady;
            LogicalLibraryName = logicalLibraryName;
            Diagnostic = diagnostic;
        }

        /// <summary>True when the vocoder library loaded with every required
        /// export present.</summary>
        public bool IsReady { get; }

        /// <summary>The logical library name that was probed.</summary>
        public string LogicalLibraryName { get; }

        /// <summary>Diagnostic message on failure, otherwise null.</summary>
        public string? Diagnostic { get; }
    }

    /// <summary>
    /// Maps the existing <see cref="INativeLibraryProbe"/> contract onto a
    /// single startup vocoder-readiness check for the Avalonia shell. The
    /// probe is invoked exactly once per <see cref="Check"/> with the fixed
    /// <c>libvocoder</c> logical name and the vocoder C ABI export manifest;
    /// success and failure results are mapped onto
    /// <see cref="VocoderReadinessResult"/> with the probe's failure
    /// diagnostic preserved verbatim. No native library is loaded and no
    /// handle is freed here — loading, export resolution and handle release
    /// are owned entirely by <see cref="INativeLibraryProbe"/> — and a
    /// throwing probe is converted into a failure result so the check can
    /// never escape.
    /// </summary>
    public sealed class VocoderReadiness
    {
        /// <summary>The logical name of the vocoder native library.</summary>
        public const string LogicalLibraryName = "libvocoder";

        private static readonly string[] RequiredExportsArray =
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

        private readonly INativeLibraryProbe probe;

        /// <summary>
        /// Creates the readiness mapper over the given probe.
        /// </summary>
        /// <param name="probe">The native-library probe that owns loading,
        /// export resolution and handle release.</param>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="probe"/> is null.</exception>
        public VocoderReadiness(INativeLibraryProbe probe)
        {
            this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        }

        /// <summary>
        /// The C ABI export symbols the vocoder library must provide, in
        /// the exact order the console consumes them.
        /// </summary>
        public static IReadOnlyList<string> RequiredExports => RequiredExportsArray;

        /// <summary>
        /// Probes the vocoder library once through the injected probe and
        /// maps the outcome onto a readiness result. A probe failure result
        /// keeps its diagnostic verbatim; a throwing probe is converted
        /// into a failure result carrying the exception message. The check
        /// never throws.
        /// </summary>
        public VocoderReadinessResult Check()
        {
            NativeLibraryProbeResult probeResult;
            try
            {
                probeResult = probe.Probe(LogicalLibraryName, RequiredExports);
            }
            catch (Exception ex)
            {
                return new VocoderReadinessResult(
                    isReady: false,
                    LogicalLibraryName,
                    ex.Message);
            }

            return new VocoderReadinessResult(
                probeResult.IsSuccess,
                probeResult.LogicalName,
                probeResult.Diagnostic);
        }
    }
}
