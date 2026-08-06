// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio.Vocoder
{
    /// <summary>
    /// The 8-export libvocoder C ABI as an injectable managed seam,
    /// mirroring the WPF DllImport surface (VocoderInterop.cs:109-135
    /// encoder, 253-281 decoder). Implementations own how the native
    /// library is loaded and how each export is invoked;
    /// <see cref="LibVocoderVoiceCodec"/> consumes only this contract so
    /// tests can substitute a managed fake. The C ABI carries no buffer
    /// lengths, so callers (the codec adapter) enforce the documented
    /// frame sizes before every native call, WPF parity.
    /// </summary>
    public interface ILibVocoderNative
    {
        /// <summary>Creates one native encoder handle for the given mode.</summary>
        /// <param name="mode">The codec mode of the encoder.</param>
        /// <returns>The native encoder handle, or <see cref="IntPtr.Zero"/>
        /// when the encoder could not be created.</returns>
        IntPtr MBEEncoder_Create(VocoderMode mode);

        /// <summary>Encodes 160 PCM samples into one mode-length codeword.</summary>
        /// <param name="handle">A native encoder handle from <see cref="MBEEncoder_Create"/>.</param>
        /// <param name="samples">The 160 input PCM samples.</param>
        /// <param name="codeword">The output codeword (9 bytes DMR, 11 bytes P25).</param>
        void MBEEncoder_Encode(IntPtr handle, short[] samples, byte[] codeword);

        /// <summary>Encodes MBE code bits into one mode-length codeword.</summary>
        /// <param name="handle">A native encoder handle from <see cref="MBEEncoder_Create"/>.</param>
        /// <param name="bits">The input code bits (49 bits DMR, 88 bits P25).</param>
        /// <param name="codeword">The output codeword (9 bytes DMR, 11 bytes P25).</param>
        void MBEEncoder_EncodeBits(IntPtr handle, byte[] bits, byte[] codeword);

        /// <summary>Deletes a native encoder handle created by <see cref="MBEEncoder_Create"/>.</summary>
        /// <param name="handle">The native encoder handle to delete.</param>
        void MBEEncoder_Delete(IntPtr handle);

        /// <summary>Creates one native decoder handle for the given mode.</summary>
        /// <param name="mode">The codec mode of the decoder.</param>
        /// <returns>The native decoder handle, or <see cref="IntPtr.Zero"/>
        /// when the decoder could not be created.</returns>
        IntPtr MBEDecoder_Create(VocoderMode mode);

        /// <summary>Decodes one mode-length codeword into 160 PCM samples.</summary>
        /// <param name="handle">A native decoder handle from <see cref="MBEDecoder_Create"/>.</param>
        /// <param name="codeword">The input codeword (9 bytes DMR, 11 bytes P25).</param>
        /// <param name="samples">The 160 output PCM samples.</param>
        /// <returns>The decode error count (informational, WPF parity), or -1
        /// when the handle is NULL/invalid.</returns>
        int MBEDecoder_Decode(IntPtr handle, byte[] codeword, short[] samples);

        /// <summary>Decodes one mode-length codeword into MBE code bits.</summary>
        /// <param name="handle">A native decoder handle from <see cref="MBEDecoder_Create"/>.</param>
        /// <param name="bits">The input codeword (9 bytes DMR, 11 bytes P25).</param>
        /// <param name="codeword">The output code bits (49 bits DMR, 88 bits P25).</param>
        /// <returns>The decode error count (informational), or -1 when the
        /// handle is NULL/invalid.</returns>
        int MBEDecoder_DecodeBits(IntPtr handle, byte[] bits, byte[] codeword);

        /// <summary>Deletes a native decoder handle created by <see cref="MBEDecoder_Create"/>.</summary>
        /// <param name="handle">The native decoder handle to delete.</param>
        void MBEDecoder_Delete(IntPtr handle);
    }
}
