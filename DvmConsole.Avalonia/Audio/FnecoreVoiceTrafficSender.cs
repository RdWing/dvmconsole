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
    /// path emits the VOICE_LC_HEADER exactly once per transmit
    /// (frame seqNo 0, RTP packet seq 0) and keeps the
    /// embedded link-control state isolated per transmit target
    /// (system name | talkgroup id | slot). The DMR RTP packet
    /// sequence is per-packet: voice frame f goes out at packet f+1
    /// (the header consumed packet 0), derived from the router's
    /// frame-domain seqNo without sender-local state. The payload d4
    /// carries the PACKET-domain dmrSeqNo (seqNo + 1; header 0,
    /// voice 1..N) and n follows WPF's pre-header-increment
    /// dmrSeqNo % 6 (first voice frame n = 0, then 2, 3, 4, 5, 0, ...).
    /// The P25 path derives the
    /// real LDU1/LDU2 alternation from the LDU sequence parity — the
    /// recorded router gap (the router always passes
    /// <c>isLdu2:false</c>). A system with no resolved adapter, a
    /// malformed talkgroup id, or (DMR) an out-of-range slot is a
    /// silent no-op: nothing is built and nothing is sent (WPF wraps
    /// the whole encode in try/catch → Log; the audio path never
    /// throws).
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

        /// <summary>
        /// P25 message header size field value inside the network frame
        /// (fnecore parity <c>FneSystemBase.P25_MSG_HDR_SIZE</c>).
        /// </summary>
        private const byte P25MsgHdrSize = 24;

        /// <summary>
        /// DMR silence frame used for the terminator pad packets
        /// (fnecore parity <c>FneSystemBase.DMR_SILENCE_DATA</c>). The
        /// fnecore constant is protected; this is the local 33-byte copy
        /// the frame builder consumes.
        /// </summary>
        private static readonly byte[] DmrSilenceData =
        {
            0x01, 0x00, 0xB9, 0xE8, 0x81, 0x52, 0x61, 0x73, 0x00, 0x2A, 0x6B,
            0xB9, 0xE8, 0x81, 0x52, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
            0x73, 0x00, 0x2A, 0x6B, 0xB9, 0xE8, 0x81, 0x52, 0x61, 0x73, 0x00,
        };

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

            if (target.Slot < 1 || target.Slot > 2)
            {
                // Out-of-range DMR slot (TransmitTarget contract,
                // IVoiceTrafficSender.cs:26): silent no-op — the
                // unchecked (byte)(Slot - 1) in the builders would
                // wrap slot 0 to 255 and emit a corrupt packet.
                return;
            }

            if (!uint.TryParse(target.TalkgroupId, out uint dstId))
            {
                // Malformed talkgroup id: silent no-op — the audio path
                // never throws (WPF wraps the encode in try/catch → Log).
                return;
            }

            if (seqNo == 0)
            {
                // WPF parity (MainWindow.DMR.cs:57-85): the
                // VOICE_LC_HEADER packet precedes the first voice frame
                // exactly once per transmit, at RTP packet sequence 0
                // (pktSeq is reset before the header block).
                SendPacket(adapter, DmrOpcode, BuildDmrHeader(adapter, target, dstId, seqNo), 0, streamId);
            }

            // WPF parity (MainWindow.DMR.cs:88-91, 119-122): the RTP
            // packet sequence is per-packet — voice frame f goes out at
            // packet f+1 because the header consumed packet 0 — while
            // the PACKET-domain dmrSeqNo (= seqNo + 1, the voice d4)
            // stays in the payload. The router's FRAME-domain seqNo is
            // only the frame counter; see BuildDmrVoice for the
            // two-domain mapping. Wraps modulo RtpCallEndSeq; 65535 is
            // reserved for the P25 call-end TDU and is never emitted on
            // voice.
            SendPacket(adapter, DmrOpcode, BuildDmrVoice(adapter, target, dstId, ambe27, seqNo), ToRtpPacketSequence(seqNo + 1), streamId);
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

            if (!uint.TryParse(target.TalkgroupId, out uint dstId))
            {
                // Malformed talkgroup id: silent no-op — the audio path
                // never throws (WPF wraps the encode in try/catch → Log).
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
                DstId = dstId,
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
                adapter.CreateP25LDU1Message(ldu, ref payload, target.SourceId, dstId);
            }

            SendPacket(adapter, P25Opcode, payload, seqNo, streamId);
        }

        /// <inheritdoc />
        public void SendDmrTerminator(TransmitTarget target, uint streamId, int nextSeqNo)
        {
            var adapter = ResolveTargetAdapter(target.SystemName);
            if (adapter is null)
            {
                // Unknown system (no adapter, or a resolver answering
                // with an adapter configured for another name): silent
                // no-op.
                return;
            }

            if (target.Slot < 1 || target.Slot > 2)
            {
                // Out-of-range DMR slot (TransmitTarget contract): silent
                // no-op, matching the voice-path guard.
                return;
            }

            if (!uint.TryParse(target.TalkgroupId, out uint dstId))
            {
                // Malformed talkgroup id: silent no-op — the audio path
                // never throws.
                return;
            }

            byte slot = (byte)(target.Slot - 1);
            uint srcId = target.SourceId;

            // Set the target's embedded LC state before the pads: every
            // pad embeds signalling, so a transmit that ends before any
            // VOICE_LC_HEADER (a short PTT with no voice frame) still
            // pads with this transmit's group link control — the
            // no-block release is valid.
            var dmrLC = new LC
            {
                FLCO = (byte)DMRFLCO.FLCO_GROUP,
                SrcId = srcId,
                DstId = dstId,
            };
            GetEmbeddedData(target).SetLC(dmrLC);

            // WPF/fnecore parity (FneSystemBase.SendDMRTerminator):
            // WPF passes the PACKET-domain sequence (channel.dmrSeqNo
            // = N+1 after N frames — the header consumed one) into the
            // terminator wrapper, while the router supplies the
            // FRAME-domain nextSeqNo (N); translate to packetBase =
            // nextSeqNo + 1 (a short PTT with no voice frame,
            // nextSeqNo 0, has no header and starts at packet 0).
            // n=(packetBase-3)%6 silence-pad frames to the next
            // six-frame boundary, then one DATA_SYNC TERMINATOR_WITH_LC
            // frame. Intentional delta: WPF computes n with a byte cast
            // over a uint-promoted difference, so packetBase < 3 yields
            // n=254 and 6-n underflows to ~4.29e9 pad frames (the
            // pad-loop hang); the port clamps to no padding, terminator
            // only — the same wire as the working n==0 case.
            int packetBase = nextSeqNo + (nextSeqNo > 0 ? 1 : 0);
            if (packetBase >= 3)
            {
                byte n = (byte)((packetBase - 3U) % 6U);
                int fill = 6 - n;
                int seqNo = packetBase;

                // fnecore parity (FneSystemBase.cs:395): the pad loop is
                // guarded by n > 0, so at a frame boundary (n == 0,
                // packetBase 3, 9, ...) zero pads are emitted and only the
                // terminator goes out.
                if (n > 0)
                {
                    for (var i = 0; i < fill; i++)
                    {
                        // fnecore parity: each pad copies the silence frame,
                        // embeds the signalling for the fixed position n
                        // (the WPF quirk — n is computed once and reused for
                        // every pad while the seq increments per pad), and
                        // goes out as a DATA_SYNC frame with the pad's seq.
                        byte[] data = new byte[DmrFrameLengthBytes];
                        Buffer.BlockCopy(DmrSilenceData, 0, data, 0, DmrFrameLengthBytes);

                        byte lcss = GetEmbeddedData(target).GetData(ref data, n);

                        var emb = new EMB
                        {
                            ColorCode = 0,
                            LCSS = lcss,
                        };
                        emb.Encode(ref data);

                        byte[] dmrpkt = new byte[DmrPacketSize];
                        adapter.CreateDMRMessage(ref dmrpkt, srcId, dstId, slot, FrameType.DATA_SYNC, (byte)seqNo, n);
                        Buffer.BlockCopy(data, 0, dmrpkt, 20, DmrFrameLengthBytes);

                        SendPacket(adapter, DmrOpcode, dmrpkt, ToRtpPacketSequence(seqNo), streamId);
                        seqNo++;
                    }
                }

                SendPacket(adapter, DmrOpcode, BuildDmrTerminator(adapter, slot, srcId, dstId, seqNo), ToRtpPacketSequence(seqNo), streamId);
            }
            else
            {
                SendPacket(adapter, DmrOpcode, BuildDmrTerminator(adapter, slot, srcId, dstId, packetBase), ToRtpPacketSequence(packetBase), streamId);
            }
        }

        /// <inheritdoc />
        public void SendP25Tdu(TransmitTarget target, uint streamId, bool grantDemand)
        {
            var adapter = ResolveTargetAdapter(target.SystemName);
            if (adapter is null)
            {
                // Unknown system (no adapter, or a resolver answering
                // with an adapter configured for another name): silent
                // no-op.
                return;
            }

            if (!uint.TryParse(target.TalkgroupId, out uint dstId))
            {
                // Malformed talkgroup id: silent no-op — the audio path
                // never throws.
                return;
            }

            byte[] payload = new byte[P25PacketSize];
            var callData = new RemoteCallData
            {
                SrcId = target.SourceId,
                DstId = dstId,
                LCO = P25Defines.LC_GROUP,
            };

            // WPF/fnecore parity (FneSystemBase.SendP25TDU): a 200-byte
            // frame with the TDU DUID, the message header size at
            // payload[23], the grant-demand control bit at payload[14],
            // sent with the RTP call-end sequence (65535) and the live
            // stream id (WPF sends stream id 0 — port delta).
            adapter.CreateP25MessageHdr((byte)P25DUID.TDU, callData, ref payload);
            payload[23] = P25MsgHdrSize;
            if (grantDemand)
            {
                payload[14] |= 0x80;
            }

            SendPacket(adapter, P25Opcode, payload, Constants.RtpCallEndSeq, streamId);
        }

        /// <summary>
        /// Resolves the adapter for a target system name and verifies it
        /// is the adapter configured for that name. A resolver that
        /// answers with an adapter configured for a DIFFERENT name (a
        /// test double ignoring its argument, or a miswired resolver) is
        /// treated as unknown — silent no-op. The production factory
        /// resolver already returns null for unknown names
        /// (FnecoreTransportFactory.ResolveAdapter), so this is a
        /// belt-and-braces guard; the comparison is case-insensitive
        /// (factory lookup parity).
        /// </summary>
        private FnecorePeerAdapter? ResolveTargetAdapter(string systemName)
        {
            var adapter = resolveAdapter(systemName);
            if (adapter is null)
            {
                return null;
            }

            return string.Equals(
                adapter.ConfiguredSystemName,
                systemName,
                StringComparison.OrdinalIgnoreCase)
                ? adapter
                : null;
        }

        /// <summary>
        /// Wraps a DMR RTP packet sequence modulo
        /// <see cref="Constants.RtpCallEndSeq"/>: sequences cycle
        /// 0..65534 and 65535 (reserved for the P25 call-end TDU) is
        /// never emitted on DMR voice, pads, or terminators. The DMR
        /// payload d4/frame-domain value is intentionally NOT wrapped
        /// — it keeps the raw sequence's unchecked byte cast (the
        /// callers pass the unwrapped value into
        /// <c>CreateDMRMessage</c>).
        /// </summary>
        private static int ToRtpPacketSequence(int seqNo)
        {
            return seqNo % Constants.RtpCallEndSeq;
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
        private byte[] BuildDmrHeader(FnecorePeerAdapter adapter, TransmitTarget target, uint dstId, int seqNo)
        {
            byte slot = (byte)(target.Slot - 1);
            uint srcId = target.SourceId;

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
        /// Two DMR sequence domains meet here. The router passes the
        /// FRAME-domain seqNo (0 = first voice frame); WPF's
        /// <c>channel.dmrSeqNo</c> counts PACKETS (the header consumed
        /// packet 0), so the payload d4 carries dmrSeqNo = seqNo + 1
        /// (header 0, voice 1..N). WPF computes
        /// <c>channel.dmrN = dmrSeqNo % 6</c> at the top of the frame
        /// block — BEFORE the header path increments dmrSeqNo — so the
        /// first voice frame (router seqNo 0, dmrSeqNo 1) carries n = 0
        /// (VOICE_SYNC) and every later frame carries
        /// n = (seqNo + 1) % 6 (VOICE, embedded signalling).
        /// </summary>
        private byte[] BuildDmrVoice(FnecorePeerAdapter adapter, TransmitTarget target, uint dstId, ReadOnlyMemory<byte> ambe27, int seqNo)
        {
            byte slot = (byte)(target.Slot - 1);
            uint srcId = target.SourceId;

            // WPF parity (MainWindow.DMR.cs:53, 85, 124): the payload
            // d4 is the PACKET-domain dmrSeqNo (router frame seqNo + 1
            // — the header consumed packet 0), and n is the dmrSeqNo
            // modulo 6 as computed BEFORE the header block incremented
            // it, so the first voice frame keeps n = 0 (VOICE_SYNC).
            int dmrSeqNo = seqNo + 1;
            byte n = seqNo == 0 ? (byte)0 : (byte)(dmrSeqNo % 6);

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

            // Generate the DMR network frame and pack the payload. The
            // packet-domain dmrSeqNo goes into d4 (WPF MainWindow.DMR.cs:
            // 119 — the voice d4 starts at 1, never 0); n drives the
            // frame type and embedded-signalling position above.
            byte[] dmrpkt = new byte[DmrPacketSize];
            adapter.CreateDMRMessage(ref dmrpkt, srcId, dstId, slot, frameType, (byte)dmrSeqNo, n);
            Buffer.BlockCopy(data, 0, dmrpkt, 20, DmrFrameLengthBytes);

            return dmrpkt;
        }

        /// <summary>
        /// Builds the 55-byte DATA_SYNC TERMINATOR_WITH_LC network
        /// packet (fnecore parity FneSystemBase.SendDMRTerminator's
        /// terminator block): the encoded group full LC is written into
        /// the 33-byte frame, then the slot type is merged into the
        /// frame's embedded-signalling gap, and the frame is packed
        /// into the network packet with a DATA_SYNC frame type.
        /// Intentional deltas: the port writes the slot type AFTER the
        /// LC encode — the fnecore order (slot type first) zeroes it,
        /// because <see cref="FullLC.Encode"/> replaces the frame buffer
        /// with the BPTC-interleaved LC — so the port's terminator
        /// carries a decodable slot type; the port encodes the target's
        /// DMR slot (WPF hardcodes slot 1 at every call site) and the
        /// caller's seq number (WPF reuses one peer packet sequence for
        /// the whole terminator run — the port's per-frame incrementing
        /// convention).
        /// </summary>
        private byte[] BuildDmrTerminator(FnecorePeerAdapter adapter, byte slot, uint srcId, uint dstId, int seqNo)
        {
            byte[] data = new byte[DmrFrameLengthBytes];

            var dmrLC = new LC
            {
                FLCO = (byte)DMRFLCO.FLCO_GROUP,
                SrcId = srcId,
                DstId = dstId,
            };

            // The BPTC encode replaces the frame buffer, so the slot
            // type must be written AFTER it: SlotType.GetData merges
            // into the embedded-signalling gap (bytes 12-20) while
            // preserving the BPTC bits it shares (byte 12 top 2, byte
            // 20 low 2).
            FullLC.Encode(dmrLC, ref data, DMRDataType.TERMINATOR_WITH_LC);

            var slotType = new SlotType
            {
                DataType = (byte)DMRDataType.TERMINATOR_WITH_LC,
            };
            slotType.GetData(ref data);

            byte[] dmrpkt = new byte[DmrPacketSize];
            adapter.CreateDMRMessage(ref dmrpkt, srcId, dstId, slot, FrameType.DATA_SYNC, (byte)seqNo, 0);
            Buffer.BlockCopy(data, 0, dmrpkt, 20, DmrFrameLengthBytes);

            return dmrpkt;
        }
    }
}
