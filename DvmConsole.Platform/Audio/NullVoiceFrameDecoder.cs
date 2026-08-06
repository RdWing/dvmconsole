// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Placeholder decoder seam that rejects every frame. Composed by the
    /// shell until the Platform-native vocoder adapter lands (a follow-on
    /// slice); a rejected frame is silently skipped by the pipelines, so
    /// the audio engine stays fully wired while decoding is inert.
    /// </summary>
    public sealed class NullVoiceFrameDecoder : IVoiceFrameDecoder
    {
        /// <inheritdoc />
        public bool TryDecode(ReadOnlyMemory<byte> voiceFrame, out short[] samples)
        {
            samples = Array.Empty<short>();
            return false;
        }
    }
}
