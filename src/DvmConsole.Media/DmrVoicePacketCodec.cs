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
        ReadOnlySpan<byte> ambe)
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

        byte[] packet = new byte[PacketBytes];
        packet[0] = (byte)'D';
        packet[1] = (byte)'M';
        packet[2] = (byte)'R';
        packet[3] = (byte)'D';
        packet[4] = frameSequence;
        WriteThreeBytes(packet, 5, sourceId);
        WriteThreeBytes(packet, 8, destinationId);
        packet[15] = (byte)(slot == 1 ? 0x80 : 0x00);
        packet[15] |= voiceSync ? (byte)0x10 : embeddedSequence;

        ambe[..13].CopyTo(packet.AsSpan(HeaderBytes, 13));
        packet[HeaderBytes + 13] = (byte)(ambe[13] & 0xF0);
        packet[HeaderBytes + 19] = (byte)(ambe[13] & 0x0F);
        ambe[14..].CopyTo(packet.AsSpan(HeaderBytes + 20, 13));
        return packet;
    }

    private static void WriteThreeBytes(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 16);
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)value;
    }
}
