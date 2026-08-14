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
}
