// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Outcome of a portable WAVE file inspection.
    /// </summary>
    /// <param name="IsValid">True when the file is a WAVE file usable by the recorder player.</param>
    /// <param name="ErrorMessage">Diagnostic message when inspection failed, otherwise null.</param>
    public readonly record struct AudioWaveInspectionResult(bool IsValid, string? ErrorMessage)
    {
        /// <summary>Creates a valid inspection result.</summary>
        public static AudioWaveInspectionResult Valid() => new(true, null);

        /// <summary>Creates an invalid inspection result with a diagnostic message.</summary>
        public static AudioWaveInspectionResult Invalid(string errorMessage) => new(false, errorMessage);
    }

    /// <summary>
    /// Inspects WAVE files without creating an audio stream or throwing
    /// user-facing file/format failures.
    /// </summary>
    public interface IAudioWaveFileInspector
    {
        /// <summary>
        /// Inspects the WAVE file at <paramref name="path"/>.
        /// </summary>
        /// <param name="path">Path of the WAVE file to inspect.</param>
        /// <returns>A typed result; failures are reported in the result, never thrown.</returns>
        AudioWaveInspectionResult Inspect(string path);
    }
}
