// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Decodes one 20 ms digital voice frame into 160 16-bit PCM samples at
    /// the console codec (8000 Hz, 16-bit, mono). Injected into
    /// <see cref="MonitorAudioPipeline"/> as a seam so voice-frame decoding
    /// stays out of the audio pipeline itself.
    /// </summary>
    public interface IVoiceFrameDecoder
    {
        /// <summary>
        /// Decodes one 20 ms voice frame into 160 16-bit PCM samples.
        /// </summary>
        /// <param name="voiceFrame">The encoded voice frame, e.g. a 27-byte DMR AMBE frame.</param>
        /// <param name="samples">The decoded 160 16-bit PCM samples when the frame decoded.</param>
        /// <returns>
        /// True when the frame decoded into <paramref name="samples"/>;
        /// false when the frame is undecodable and must be skipped.
        /// </returns>
        bool TryDecode(ReadOnlyMemory<byte> voiceFrame, out short[] samples);
    }
}
