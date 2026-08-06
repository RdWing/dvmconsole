// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Decodes one voice codeword into 160 16-bit PCM samples at the
    /// console codec (8000 Hz, 16-bit, mono). The seam consumes
    /// PER-CODEWORD units — a single 9-byte DMR AMBE codeword or a single
    /// 11-byte P25 IMBE codeword — each decoding to exactly 160 samples
    /// (320 bytes little-endian), matching the WPF decode granularity
    /// (MainWindow.DMR.cs:182-203, MainWindow.P25.cs:301-333). Injected
    /// into <see cref="MonitorAudioPipeline"/> as a seam so voice-frame
    /// decoding stays out of the audio pipeline itself.
    /// </summary>
    public interface IVoiceFrameDecoder
    {
        /// <summary>
        /// Decodes one voice codeword into 160 16-bit PCM samples.
        /// </summary>
        /// <param name="voiceFrame">
        /// The encoded voice codeword: 9 bytes for DMR, 11 bytes for P25.
        /// </param>
        /// <param name="samples">The decoded 160 16-bit PCM samples when the codeword decoded.</param>
        /// <returns>
        /// True when the codeword decoded into <paramref name="samples"/>;
        /// false when the codeword is undecodable and must be skipped.
        /// </returns>
        bool TryDecode(ReadOnlyMemory<byte> voiceFrame, out short[] samples);
    }
}
