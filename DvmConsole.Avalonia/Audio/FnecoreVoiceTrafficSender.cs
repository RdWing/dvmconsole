// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using fnecore;
using fnecore.DMR;
using fnecore.P25;

namespace DvmConsole.Avalonia.Audio
{
    /// <summary>
    /// Observes assembled voice packets before they reach the network.
    /// The traffic sender resolves the target system's
    /// <see cref="FnecorePeerAdapter"/>, assembles the WPF-exact packet
    /// and delivers it either through this seam (headless tests) or
    /// directly through the adapter (the default sink).
    /// </summary>
    public interface IPacketSink
    {
        /// <summary>
        /// Delivers one assembled master-traffic packet.
        /// </summary>
        /// <param name="opcode">The network protocol opcode tuple.</param>
        /// <param name="payload">The assembled network frame payload.</param>
        /// <param name="seq">The RTP packet sequence.</param>
        /// <param name="streamId">The transmit stream id.</param>
        void Send(Tuple<byte, byte> opcode, byte[] payload, ushort seq, uint streamId);
    }

    /// <summary>
    /// The real <see cref="IVoiceTrafficSender"/>: assembles WPF-exact
    /// DMR and P25 packets and delivers them through the target
    /// system's <see cref="FnecorePeerAdapter"/> (WPF
    /// MainWindow.DMR.cs:62-122 / MainWindow.P25.cs parity). The DMR
    /// path emits the VOICE_LC_HEADER before the voice frame at the
    /// start of every six-frame slot (seqNo % 6 == 0) and keeps the
    /// embedded link-control state isolated per transmit target
    /// (system name | talkgroup id | slot). The P25 path derives the
    /// real LDU1/LDU2 alternation from the LDU sequence parity — the
    /// recorded router gap (the router always passes
    /// <c>isLdu2:false</c>). A system with no resolved adapter is a
    /// silent no-op: nothing is built and nothing is sent.
    /// </summary>
    public sealed class FnecoreVoiceTrafficSender : IVoiceTrafficSender
    {
        /// <summary>
        /// DMR protocol opcode (fnecore parity).
        /// </summary>
        private static readonly Tuple<byte, byte> DmrOpcode =
            new Tuple<byte, byte>(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_DMR);

        /// <summary>
        /// P25 protocol opcode (fnecore parity).
        /// </summary>
        private static readonly Tuple<byte, byte> P25Opcode =
            new Tuple<byte, byte>(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_P25);

        /// <summary>
        /// Complete DMR network frame size (WPF parity
        /// FneSystemBase.DMR_PACKET_SIZE).
        /// </summary>
        private const int DmrPacketSize = 55;

        /// <summary>
        /// DMR voice frame size inside the network frame (WPF parity
        /// FneSystemBase.DMR_FRAME_LENGTH_BYTES).
        /// </summary>
        private const int DmrFrameLengthBytes = 33;

        /// <summary>
        /// Complete P25 network frame size (WPF parity).
        /// </summary>
        private const int P25PacketSize = 200;

        private readonly Func<string, FnecorePeerAdapter?> resolveAdapter;
        private readonly IPacketSink? sink;

        /// <summary>
        /// Per-target embedded link-control state (WPF parity: one
        /// EmbeddedData per channel), keyed by
        /// (SystemName|TalkgroupId|Slot) so concurrent transmits never
        /// share embedded signalling state.
        /// </summary>
        private readonly Dictionary<string, EmbeddedData> embeddedDataByTarget =
            new Dictionary<string, EmbeddedData>();

        /// <summary>
        /// Creates the sender over the given adapter resolver.
        /// </summary>
        /// <param name="resolveAdapter">Resolves the adapter for a system name; null when unknown.</param>
        /// <param name="sink">Optional packet observation seam; when null the default sink sends through the resolved adapter.</param>
        public FnecoreVoiceTrafficSender(
            Func<string, FnecorePeerAdapter?> resolveAdapter,
            IPacketSink? sink = null)
        {
            this.resolveAdapter = resolveAdapter
                ?? throw new ArgumentNullException(nameof(resolveAdapter));
            this.sink = sink;
        }

        /// <inheritdoc />
        public void SendDmrVoice(TransmitTarget target, ReadOnlyMemory<byte> ambe27, uint streamId, int seqNo)
        {
            var adapter = resolveAdapter(target.SystemName);
            if (adapter is null)
            {
                // Unknown or not-yet-started system: silent no-op.
                return;
            }

            if (seqNo % 6 == 0)
            {
                // WPF cadence (MainWindow.DMR.cs:57-86): the
                // VOICE_LC_HEADER packet precedes the voice frame at the
                // start of each six-frame slot.
                SendPacket(adapter, DmrOpcode, BuildDmrHeader(adapter, target, seqNo), seqNo, streamId);
            }

            SendPacket(adapter, DmrOpcode, BuildDmrVoice(adapter, target, ambe27, seqNo), seqNo, streamId);
        }

        /// <inheritdoc />
        public void SendP25Ldu(TransmitTarget target, bool isLdu2, ReadOnlyMemory<byte> ldu225, uint streamId, int seqNo)
        {
            var adapter = resolveAdapter(target.SystemName);
            if (adapter is null)
            {
                // Unknown or not-yet-started system: silent no-op.
                return;
            }

            // The router always passes isLdu2:false; the real
            // LDU1/LDU2 alternation is derived from the LDU sequence
            // parity (recorded WPF gap fix).
            bool realLdu2 = (seqNo & 1) == 1;

            byte[] ldu = ldu225.ToArray();
            byte[] payload = new byte[P25PacketSize];
            var callData = new RemoteCallData
            {
                SrcId = target.SourceId,
                DstId = uint.Parse(target.TalkgroupId),
                LCO = P25Defines.LC_GROUP,
            };

            // WPF parity MainWindow.P25.cs:251-281: header then the
            // DFSI record packing into the 200-byte payload.
            adapter.CreateP25MessageHdr((byte)(realLdu2 ? P25DUID.LDU2 : P25DUID.LDU1), callData, ref payload);
            if (realLdu2)
            {
                adapter.CreateP25LDU2Message(ldu, ref payload);
            }
            else
            {
                adapter.CreateP25LDU1Message(ldu, ref payload, target.SourceId, uint.Parse(target.TalkgroupId));
            }

            SendPacket(adapter, P25Opcode, payload, seqNo, streamId);
        }

        /// <summary>
        /// Delivers one assembled packet through the injected sink, or
        /// through the resolved adapter's master traffic when no sink
        /// was injected (the default sink).
        /// </summary>
        private void SendPacket(FnecorePeerAdapter adapter, Tuple<byte, byte> opcode, byte[] payload, int seqNo, uint streamId)
        {
            if (sink is { } s)
            {
                s.Send(opcode, payload, (ushort)seqNo, streamId);
            }
            else
            {
                adapter.SendMasterTraffic(opcode, payload, (ushort)seqNo, streamId);
            }
        }

        /// <summary>
        /// Returns the per-target embedded link-control state, creating
        /// it on first use. Keyed by (SystemName|TalkgroupId|Slot) so
        /// the embedded LC of one transmit never leaks into another.
        /// </summary>
        private EmbeddedData GetEmbeddedData(TransmitTarget target)
        {
            string key = $"{target.SystemName}|{target.TalkgroupId}|slot:{target.Slot}";
            if (!embeddedDataByTarget.TryGetValue(key, out var embedded))
            {
                embedded = new EmbeddedData();
                embeddedDataByTarget[key] = embedded;
            }

            return embedded;
        }

        /// <summary>
        /// Builds the 55-byte VOICE_LC_HEADER network packet. WPF
        /// parity MainWindow.DMR.cs:62-83: the group LC is embedded for
        /// the target, then the slot type and full LC are written into
        /// the 33-byte frame, and the frame is packed into the network
        /// packet with a VOICE_SYNC frame type.
        /// </summary>
        private byte[] BuildDmrHeader(FnecorePeerAdapter adapter, TransmitTarget target, int seqNo)
        {
            byte slot = (byte)(target.Slot - 1);
            uint srcId = target.SourceId;
            uint dstId = uint.Parse(target.TalkgroupId);

            // Generate the DMR LC and seed the target's embedded state.
            var dmrLC = new LC
            {
                FLCO = (byte)DMRFLCO.FLCO_GROUP,
                SrcId = srcId,
                DstId = dstId,
            };
            GetEmbeddedData(target).SetLC(dmrLC);

            // Generate the slot type (VOICE_LC_HEADER).
            byte[] data = new byte[DmrFrameLengthBytes];
            var slotType = new SlotType
            {
                DataType = (byte)DMRDataType.VOICE_LC_HEADER,
            };
            slotType.GetData(ref data);

            // Encode the full link control into the frame.
            FullLC.Encode(dmrLC, ref data, DMRDataType.VOICE_LC_HEADER);

            // Generate the DMR network frame and pack the payload.
            byte[] dmrpkt = new byte[DmrPacketSize];
            adapter.CreateDMRMessage(ref dmrpkt, srcId, dstId, slot, FrameType.VOICE_SYNC, (byte)seqNo, 0);
            Buffer.BlockCopy(data, 0, dmrpkt, 20, DmrFrameLengthBytes);

            return dmrpkt;
        }

        /// <summary>
        /// Builds the 55-byte DMR voice network packet. WPF parity
        /// MainWindow.DMR.cs:93-122: the 27 AMBE bytes are packed with
        /// the nibble split (ambe[13] high nibble to data[13], low
        /// nibble to data[19]), the sync frame is VOICE_SYNC and every
        /// other frame is VOICE with the embedded signalling (EMB with
        /// the LCSS from the target's embedded state).
        /// </summary>
        private byte[] BuildDmrVoice(FnecorePeerAdapter adapter, TransmitTarget target, ReadOnlyMemory<byte> ambe27, int seqNo)
        {
            byte slot = (byte)(target.Slot - 1);
            uint srcId = target.SourceId;
            uint dstId = uint.Parse(target.TalkgroupId);
            byte n = (byte)(seqNo % 6);

            byte[] ambe = ambe27.ToArray();
            byte[] data = new byte[DmrFrameLengthBytes];

            Buffer.BlockCopy(ambe, 0, data, 0, 13);
            data[13U] = (byte)(ambe[13U] & 0xF0);
            data[19U] = (byte)(ambe[13U] & 0x0F);
            Buffer.BlockCopy(ambe, 14, data, 20, 13);

            FrameType frameType = FrameType.VOICE_SYNC;
            if (n != 0)
            {
                frameType = FrameType.VOICE;

                // Embedded signalling for this position, carrying the
                // target's link control.
                byte lcss = GetEmbeddedData(target).GetData(ref data, n);

                var emb = new EMB
                {
                    ColorCode = 0,
                    LCSS = lcss,
                };
                emb.Encode(ref data);
            }

            // Generate the DMR network frame and pack the payload.
            byte[] dmrpkt = new byte[DmrPacketSize];
            adapter.CreateDMRMessage(ref dmrpkt, srcId, dstId, slot, frameType, (byte)seqNo, n);
            Buffer.BlockCopy(data, 0, dmrpkt, 20, DmrFrameLengthBytes);

            return dmrpkt;
        }
    }
}
