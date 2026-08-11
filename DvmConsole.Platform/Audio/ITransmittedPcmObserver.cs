// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Observes PCM that has been selected for transmission to a resolved
    /// target. The callback receives the same 20 ms PCM frame the transmit
    /// pump uses for encoding and network delivery.
    /// </summary>
    public interface ITransmittedPcmObserver
    {
        void ObserveTransmittedPcm(TransmitTarget target, ReadOnlyMemory<byte> pcm);
    }
}
