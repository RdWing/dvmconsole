// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
namespace DvmConsole.Platform.Audio.Vocoder
{
    /// <summary>
    /// Digital voice codec mode of a native libvocoder encoder/decoder
    /// handle. Mirrors the WPF <c>MBE_MODE</c> enum (VocoderInterop.cs:23-27)
    /// with identical integer values, which the C ABI consumes directly:
    /// <see cref="Dmr"/> is the 9-byte AMBE mode and <see cref="P25"/> is
    /// the 11-byte 88-bit IMBE mode.
    /// </summary>
    public enum VocoderMode
    {
        /// <summary>Motorola DMR: 9-byte AMBE codewords (49 code bits).</summary>
        Dmr = 0,

        /// <summary>P25 Phase 1: 11-byte IMBE codewords (88 code bits).</summary>
        P25 = 1,
    }
}
