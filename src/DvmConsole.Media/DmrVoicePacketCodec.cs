using fnecore.DMR;

namespace DvmConsole.Media;

/// <summary>
/// Extracts the three AMBE codewords carried by one DMR FNE voice packet.
/// The layout matches the legacy DMRD/FNE packet: a 20-byte RTP/FNE header,
/// followed by a 33-byte DMR frame and a two-byte trailer.
/// </summary>
public static class DmrVoicePacketCodec
{
    public const int HeaderBytes = 20;
    public const int FrameBytes = 33;
    public const int PacketBytes = 55;
    public const int CodewordBytes = 9;
    public const int CodewordsPerPacket = 3;
    public const int AmbeBytes = CodewordBytes * CodewordsPerPacket;
    public const ushort RtpCallEndSequence = ushort.MaxValue;

    public readonly record struct DmrEncryptionMetadata(byte AlgorithmId, byte KeyId);

    public static bool TryExtractAmbe(ReadOnlySpan<byte> packet, Span<byte> ambe)
    {
        if (packet.Length < PacketBytes || ambe.Length < AmbeBytes)
            return false;

        ReadOnlySpan<byte> frame = packet.Slice(HeaderBytes, FrameBytes);
        frame[..13].CopyTo(ambe);
        ambe[13] = (byte)((frame[13] & 0xF0) | (frame[19] & 0x0F));
        frame.Slice(20, 13).CopyTo(ambe[14..]);
        return true;
    }

    public static byte[] ExtractAmbe(ReadOnlySpan<byte> packet)
    {
        byte[] ambe = new byte[AmbeBytes];
        if (!TryExtractAmbe(packet, ambe))
            throw new ArgumentException("The DMR packet does not contain a complete voice frame.", nameof(packet));
        return ambe;
    }

    /// <summary>
    /// Reads the DMR privacy indicator link-control header from a complete
    /// DMR network packet. The FNE payload contains the 33-byte BPTC frame
    /// after the 20-byte network header; malformed or CRC-invalid PI frames
    /// are treated as unknown rather than as clear traffic.
    /// </summary>
    public static bool TryExtractEncryptionMetadata(
        ReadOnlySpan<byte> packet,
        out DmrEncryptionMetadata metadata)
    {
        metadata = default;
        if (packet.Length < PacketBytes)
            return false;

        try
        {
            byte[] frame = packet.Slice(HeaderBytes, FrameBytes).ToArray();
            PrivacyLC? privacy = FullLC.DecodePI(frame);
            if (privacy is null)
                return false;

            metadata = new DmrEncryptionMetadata(privacy.AlgId, (byte)privacy.KId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the fixed-size DMR voice packet used by the FNE traffic API.
    /// Link-control header/terminator construction remains in the TX session
    /// layer; this method only maps one AMBE slot into the wire frame.
    /// </summary>
    public static byte[] CreateVoicePacket(
        uint sourceId,
        uint destinationId,
        byte slot,
        bool voiceSync,
        byte embeddedSequence,
        byte frameSequence,
        ReadOnlySpan<byte> ambe,
        EmbeddedData? embeddedData = null)
    {
        if (sourceId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (slot > 1)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (embeddedSequence > 5)
            throw new ArgumentOutOfRangeException(nameof(embeddedSequence));
        if (ambe.Length < AmbeBytes)
            throw new ArgumentException($"AMBE data must contain {AmbeBytes} bytes.", nameof(ambe));

        byte[] packet = CreatePacketHeader(sourceId, destinationId, slot, frameSequence);
        packet[15] |= voiceSync ? (byte)0x10 : embeddedSequence;

        byte[] frame = new byte[FrameBytes];
        ambe[..13].CopyTo(frame);
        frame[13] = (byte)(ambe[13] & 0xF0);
        frame[19] = (byte)(ambe[13] & 0x0F);
        ambe[14..].CopyTo(frame.AsSpan(20, 13));

        if (!voiceSync && embeddedData is not null)
        {
            byte lcss = embeddedData.GetData(ref frame, embeddedSequence);
            new EMB { ColorCode = 0, LCSS = lcss }.Encode(ref frame);
        }

        frame.CopyTo(packet.AsSpan(HeaderBytes));
        return packet;
    }

    /// <summary>
    /// Creates the DMR voice link-control header that starts a group call.
    /// </summary>
    public static byte[] CreateVoiceLcHeaderPacket(
        uint sourceId,
        uint destinationId,
        byte slot,
        byte frameSequence)
    {
        return CreateControlPacket(sourceId, destinationId, slot, frameSequence, DMRDataType.VOICE_LC_HEADER);
    }

    /// <summary>
    /// Creates the DMR terminator with link-control that ends a group call.
    /// </summary>
    public static byte[] CreateTerminatorPacket(
        uint sourceId,
        uint destinationId,
        byte slot,
        byte frameSequence)
    {
        return CreateControlPacket(sourceId, destinationId, slot, frameSequence, DMRDataType.TERMINATOR_WITH_LC);
    }

    private static byte[] CreateControlPacket(
        uint sourceId,
        uint destinationId,
        byte slot,
        byte frameSequence,
        DMRDataType dataType)
    {
        if (sourceId == 0 || sourceId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId == 0 || destinationId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (slot > 1)
            throw new ArgumentOutOfRangeException(nameof(slot));

        byte[] packet = CreatePacketHeader(sourceId, destinationId, slot, frameSequence);
        // The network header carries the FNE frame type, not the DMR slot
        // data type. Both LC headers and terminators are DATA_SYNC (0x02);
        // the slot type inside the 33-byte DMR frame distinguishes them.
        packet[15] |= (byte)(0x20 | (byte)fnecore.FrameType.DATA_SYNC);

        byte[] frame = new byte[FrameBytes];
        new SlotType { ColorCode = 0, DataType = (byte)dataType }.GetData(ref frame);
        var lc = new LC
        {
            FLCO = (byte)DMRFLCO.FLCO_GROUP,
            SrcId = sourceId,
            DstId = destinationId
        };
        FullLC.Encode(lc, ref frame, dataType);
        frame.CopyTo(packet.AsSpan(HeaderBytes));
        return packet;
    }

    private static byte[] CreatePacketHeader(uint sourceId, uint destinationId, byte slot, byte frameSequence)
    {
        byte[] packet = new byte[PacketBytes];
        packet[0] = (byte)'D';
        packet[1] = (byte)'M';
        packet[2] = (byte)'R';
        packet[3] = (byte)'D';
        packet[4] = frameSequence;
        WriteThreeBytes(packet, 5, sourceId);
        WriteThreeBytes(packet, 8, destinationId);
        // FNE decodes the high bit as zero-based slot 1 (displayed as timeslot 2).
        packet[15] = (byte)(slot == 1 ? 0x80 : 0x00);
        return packet;
    }

    private static void WriteThreeBytes(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 16);
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)value;
    }
}
