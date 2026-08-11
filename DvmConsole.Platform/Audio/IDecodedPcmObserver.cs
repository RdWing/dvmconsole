// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Observes successfully decoded receive PCM before optional local-monitor
    /// playback. Implementations must not assume that a monitor output device
    /// exists; the router invokes this boundary independently of monitor
    /// selection. One callback represents one decoded 20 ms codeword (320
    /// bytes, 8 kHz, 16-bit, mono) in receive order.
    /// </summary>
    public interface IDecodedPcmObserver
    {
        /// <summary>
        /// Receives one decoded PCM codeword in receive order.
        /// </summary>
        /// <param name="talkgroupKey">Stable routed talkgroup identity.</param>
        /// <param name="mode">DMR or P25 voice mode.</param>
        /// <param name="pcm">Exactly one decoded 320-byte PCM frame.</param>
        void ObserveDecodedPcm(
            string talkgroupKey,
            VoiceMode mode,
            ReadOnlyMemory<byte> pcm);
    }
}
