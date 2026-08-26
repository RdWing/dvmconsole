using fnecore.DMR;

namespace DvmConsole.Media;

// Extracts the three AMBE codewords carried by one DMR FNE voice packet.
// The layout matches the legacy DMRD/FNE packet: a 20-byte RTP/FNE header,
// followed by a 33-byte DMR frame and a two-byte trailer.
public static class DmrVoicePacketCodec
{
    public const int HeaderBytes = 20;
    public const int FrameBytes = 33;
    public const int PacketBytes = 55;
    public const int CodewordBytes = 9;
    public const int CodewordsPerPacket = 3;
    public const int AmbeBytes = CodewordBytes * CodewordsPerPacket;
    public const ushort RtpCallEndSequence = ushort.MaxValue;

    public readonly record struct DmrEncryptionMetadata(
        byte AlgorithmId,
        byte KeyId,
        byte FeatureId,
        uint DestinationId,
        bool Group,
        byte[] MessageIndicator);

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

    // Reads the DMR privacy indicator link-control header from a complete
    // DMR network packet. The FNE payload contains the 33-byte BPTC frame
    // after the 20-byte network header; malformed or CRC-invalid PI frames
    // are treated as unknown rather than as clear traffic.
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
            var slotType = new SlotType(frame);
            if (slotType.DataType != (byte)DMRDataType.VOICE_PI_HEADER)
                return false;
            PrivacyLC? privacy = FullLC.DecodePI(frame);
            if (privacy is null)
                return false;
            if (privacy.FID != DmrPrivacyAlgorithms.FeatureId ||
                privacy.KId == 0 || privacy.AlgId is not (
                DmrPrivacyAlgorithms.Arc4 or
                DmrPrivacyAlgorithms.DesOfb or
                DmrPrivacyAlgorithms.Aes256))
            {
                return false;
            }

            byte[] raw = privacy.GetBytes();
            metadata = new DmrEncryptionMetadata(
                privacy.AlgId,
                (byte)privacy.KId,
                privacy.FID,
                privacy.DstId,
                privacy.Group,
                raw[3..7]);
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

    public static bool IsPrivacyIndicator(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < PacketBytes)
            return false;
        // The low nibble of the FNE DMR frame-type byte is authoritative.
        // Decoding only the in-burst slot type can classify a voice-LC header
        // as PI after FEC correction, even though its network envelope is 0x21.
        if ((packet[15] & 0x3F) != 0x20)
            return false;

        try
        {
            var slotType = new SlotType(packet.Slice(HeaderBytes, FrameBytes).ToArray());
            return slotType.DataType == (byte)DMRDataType.VOICE_PI_HEADER;
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

    public static bool TryExtractVoiceEncryptionState(ReadOnlySpan<byte> packet, out bool encrypted)
    {
        encrypted = false;
        if (packet.Length < PacketBytes || (packet[15] & 0x3F) != 0x21)
            return false;

        try
        {
            byte[] frame = packet.Slice(HeaderBytes, FrameBytes).ToArray();
            var slotType = new SlotType(frame);
            if (slotType.DataType != (byte)DMRDataType.VOICE_LC_HEADER)
                return false;
            LC? linkControl = FullLC.Decode(frame, DMRDataType.VOICE_LC_HEADER);
            if (linkControl is null)
                return false;
            encrypted = linkControl.Encrypted;
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

    // Builds the fixed-size DMR voice packet used by the FNE traffic API.
    // Link-control header/terminator construction remains in the TX session
    // layer; this method only maps one AMBE slot into the wire frame.
    public static byte[] CreateVoicePacket(
        uint sourceId,
        uint destinationId,
        byte slot,
        bool voiceSync,
        byte embeddedSequence,
        byte frameSequence,
        ReadOnlySpan<byte> ambe,
        EmbeddedData? embeddedData = null,
        DmrBurstFSignaling? burstFSignaling = null)
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

        if (!voiceSync)
        {
            byte lcss = 0;
            if (embeddedSequence is >= 1 and <= 4 && embeddedData is not null)
                lcss = embeddedData.GetData(ref frame, embeddedSequence);
            else if (embeddedSequence == 5 && burstFSignaling is { } signaling)
                signaling.Encode(frame);
            new EMB
            {
                ColorCode = 0,
                PI = embeddedSequence == 5 && burstFSignaling?.IsReverseChannel == true,
                LCSS = lcss
            }.Encode(ref frame);
        }

        frame.CopyTo(packet.AsSpan(HeaderBytes));
        return packet;
    }

    public static bool TryExtractBurstFSignaling(
        ReadOnlySpan<byte> packet,
        out DmrBurstFSignaling signaling)
    {
        signaling = default;
        if (packet.Length < PacketBytes || (packet[15] & 0x0F) != 5)
            return false;

        byte[] frame = packet.Slice(HeaderBytes, FrameBytes).ToArray();
        var embedded = new EMB();
        embedded.Decode(frame);
        if (embedded.LCSS != 0)
            return false;
        return DmrBurstFSignaling.TryDecode(frame, embedded.PI, out signaling);
    }

    // Creates the DMR voice link-control header that starts a group call.
    public static byte[] CreateVoiceLcHeaderPacket(
        uint sourceId,
        uint destinationId,
        byte slot,
        byte frameSequence,
        bool encrypted = false)
    {
        return CreateControlPacket(
            sourceId,
            destinationId,
            slot,
            frameSequence,
            DMRDataType.VOICE_LC_HEADER,
            encrypted);
    }

    // Creates the DMR Association privacy-indicator header that carries the
    // algorithm, one-byte key ID, and four-byte message indicator.
    public static byte[] CreatePrivacyIndicatorPacket(
        uint sourceId,
        uint destinationId,
        byte slot,
        byte frameSequence,
        DmrPrivacyOptions privacy)
    {
        if (sourceId == 0 || sourceId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId == 0 || destinationId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (slot > 1)
            throw new ArgumentOutOfRangeException(nameof(slot));
        ArgumentNullException.ThrowIfNull(privacy);

        byte[] packet = CreatePacketHeader(sourceId, destinationId, slot, frameSequence);
        // VOICE_PI_HEADER is DMR data type zero, so its DATA_SYNC envelope is
        // 0x20. Using the generic FNE DATA_SYNC enum value here produces 0x22,
        // which a master correctly interprets as a call terminator.
        packet[15] |= (byte)(0x20 | (byte)DMRDataType.VOICE_PI_HEADER);
        byte[] frame = new byte[FrameBytes];
        byte[] raw = new byte[10];
        raw[0] = (byte)(0x20 | privacy.AlgorithmId);
        raw[1] = DmrPrivacyAlgorithms.FeatureId;
        raw[2] = privacy.KeyId;
        privacy.MessageIndicator.Span.CopyTo(raw.AsSpan(3, DmrPrivacyAlgorithms.MessageIndicatorBytes));
        WriteThreeBytes(raw, 7, destinationId);
        FullLC.EncodePI(new PrivacyLC(raw), ref frame);
        new SlotType { ColorCode = 0, DataType = (byte)DMRDataType.VOICE_PI_HEADER }.GetData(ref frame);
        frame.CopyTo(packet.AsSpan(HeaderBytes));
        return packet;
    }

    // Creates the DMR terminator with link-control that ends a group call.
    public static byte[] CreateTerminatorPacket(
        uint sourceId,
        uint destinationId,
        byte slot,
        byte frameSequence,
        bool encrypted = false)
    {
        return CreateControlPacket(
            sourceId,
            destinationId,
            slot,
            frameSequence,
            DMRDataType.TERMINATOR_WITH_LC,
            encrypted);
    }

    private static byte[] CreateControlPacket(
        uint sourceId,
        uint destinationId,
        byte slot,
        byte frameSequence,
        DMRDataType dataType,
        bool encrypted = false)
    {
        if (sourceId == 0 || sourceId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId == 0 || destinationId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (slot > 1)
            throw new ArgumentOutOfRangeException(nameof(slot));

        byte[] packet = CreatePacketHeader(sourceId, destinationId, slot, frameSequence);
        // DMR control bursts use the DATA_SYNC envelope. The low nibble is the
        // DMR data type; slot and private-call flags occupy the high bits.
        packet[15] |= (byte)(0x20 | (byte)dataType);

        byte[] frame = new byte[FrameBytes];
        var lc = new LC
        {
            FLCO = (byte)DMRFLCO.FLCO_GROUP,
            // DMR Association privacy uses FID 0x10 on the voice LC as well as
            // on the PI LC. The encrypted service option is a separate bit and
            // must also remain set for the duration of a protected call.
            FID = encrypted ? DmrPrivacyAlgorithms.FeatureId : (byte)0,
            Encrypted = encrypted,
            SrcId = sourceId,
            DstId = destinationId
        };
        FullLC.Encode(lc, ref frame, dataType);
        new SlotType { ColorCode = 0, DataType = (byte)dataType }.GetData(ref frame);
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
