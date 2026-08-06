// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Encodes one 20 ms PCM voice frame into one voice codeword at the
    /// console codec (8000 Hz, 16-bit, mono). Injected into
    /// <see cref="TalkgroupAudioRouter"/> as the transmit-side seam
    /// counterpart of <see cref="IVoiceFrameDecoder"/> so vocoder
    /// encoding stays out of the audio pipeline itself. The seam
    /// consumes PER-CODEWORD units: exactly 160 little-endian 16-bit
    /// samples in, one 9-byte DMR AMBE codeword or one 11-byte P25 IMBE
    /// codeword out (WPF encode parity: MainWindow.DMR.cs:132-136,
    /// MainWindow.P25.cs:301-333).
    /// </summary>
    public interface IVoiceFrameEncoder
    {
        /// <summary>
        /// Encodes one 20 ms PCM voice frame into one voice codeword.
        /// </summary>
        /// <param name="samples">The 160 16-bit little-endian PCM samples to encode.</param>
        /// <param name="codeword">
        /// The encoded codeword when the frame encoded: 9 bytes for DMR
        /// or 11 bytes for P25 (the caller knows the mode).
        /// </param>
        /// <returns>
        /// True when the samples encoded into <paramref name="codeword"/>;
        /// false when the frame is unencodable and must be skipped.
        /// </returns>
        bool TryEncode(ReadOnlyMemory<short> samples, out byte[] codeword);
    }
}
