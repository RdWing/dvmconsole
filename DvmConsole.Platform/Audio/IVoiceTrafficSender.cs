// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Digital voice mode of a transmit target.
    /// </summary>
    public enum VoiceMode
    {
        /// <summary>Motorola DMR: 3 x 9-byte AMBE codewords per 60 ms frame.</summary>
        Dmr,

        /// <summary>P25 Phase 1: 9 x 11-byte IMBE codewords per 180 ms LDU.</summary>
        P25,
    }

    /// <summary>
    /// Immutable transmit target: the FNE system, talkgroup, DMR slot,
    /// voice mode and source radio id a transmitted voice unit is sent
    /// to/from.
    /// </summary>
    /// <param name="SystemName">FNE system the traffic is sent through.</param>
    /// <param name="TalkgroupId">Destination talkgroup id.</param>
    /// <param name="Slot">DMR slot (1 or 2); unused for P25.</param>
    /// <param name="Mode">Voice mode of the transmitted traffic.</param>
    /// <param name="SourceId">Source radio id of the transmitting console.</param>
    public readonly record struct TransmitTarget(
        string SystemName,
        string TalkgroupId,
        byte Slot,
        VoiceMode Mode,
        uint SourceId);

    /// <summary>
    /// Transmit-side traffic seam: receives complete DMR voice frames
    /// (27-byte AMBE triples) and P25 LDUs (225 bytes) for delivery to
    /// the FNE. Implementations own the network delivery; the audio
    /// engine only accumulates and hands off units.
    /// </summary>
    public interface IVoiceTrafficSender
    {
        /// <summary>
        /// Sends one complete DMR voice frame: three 9-byte AMBE
        /// codewords packed into a 27-byte unit.
        /// </summary>
        /// <param name="target">The transmit target.</param>
        /// <param name="ambe27">The 27-byte DMR AMBE frame.</param>
        /// <param name="streamId">Monotonically increasing stream id for the transmit.</param>
        /// <param name="seqNo">Sequence number of this frame within the transmit.</param>
        void SendDmrVoice(TransmitTarget target, ReadOnlyMemory<byte> ambe27, uint streamId, int seqNo);

        /// <summary>
        /// Sends one complete P25 logical data unit: nine 11-byte IMBE
        /// codewords packed into a 225-byte LDU.
        /// </summary>
        /// <param name="target">The transmit target.</param>
        /// <param name="isLdu2">True for an LDU2 (second half of a 360 ms superframe).</param>
        /// <param name="ldu225">The 225-byte P25 LDU.</param>
        /// <param name="streamId">Monotonically increasing stream id for the transmit.</param>
        /// <param name="seqNo">Sequence number of this LDU within the transmit.</param>
        void SendP25Ldu(TransmitTarget target, bool isLdu2, ReadOnlyMemory<byte> ldu225, uint streamId, int seqNo);
    }

    /// <summary>
    /// No-op traffic sender that counts delivered units. Used by the
    /// shell until the fnecore adapter lands; never throws and performs
    /// no network activity.
    /// </summary>
    public sealed class StubVoiceTrafficSender : IVoiceTrafficSender
    {
        /// <summary>Number of DMR voice frames sent.</summary>
        public int DmrFrameCount { get; set; }

        /// <summary>Number of P25 LDUs sent.</summary>
        public int P25LduCount { get; set; }

        /// <inheritdoc />
        public void SendDmrVoice(TransmitTarget target, ReadOnlyMemory<byte> ambe27, uint streamId, int seqNo)
            => DmrFrameCount++;

        /// <inheritdoc />
        public void SendP25Ldu(TransmitTarget target, bool isLdu2, ReadOnlyMemory<byte> ldu225, uint streamId, int seqNo)
            => P25LduCount++;
    }
}
