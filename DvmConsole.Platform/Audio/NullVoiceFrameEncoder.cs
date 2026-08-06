// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Placeholder encoder seam that rejects every frame. Composed by the
    /// shell until the Platform-native vocoder adapter lands (a follow-on
    /// slice); a rejected frame is silently skipped by the router, so the
    /// audio engine stays fully wired while encoding is inert.
    /// </summary>
    public sealed class NullVoiceFrameEncoder : IVoiceFrameEncoder
    {
        /// <inheritdoc />
        public bool TryEncode(ReadOnlyMemory<short> samples, out byte[] codeword)
        {
            codeword = Array.Empty<byte>();
            return false;
        }
    }
}
