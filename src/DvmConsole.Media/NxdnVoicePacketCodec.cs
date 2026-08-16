namespace DvmConsole.Media;

// Extracts the raw NXDN frame from an FNE NXDD packet. The returned 48-byte
// frame is still encoded NXDN data; it must not be passed to a DMR AMBE
// decoder or treated as PCM.
public static class NxdnVoicePacketCodec
{
    public const int HeaderBytes = 20;
    public const int FrameBytes = 48;
    public const int TrailerBytes = 2;
    public const int PacketBytes = HeaderBytes + FrameBytes + TrailerBytes;

    public static bool TryExtractFrame(ReadOnlySpan<byte> packet, Span<byte> frame)
    {
        if (packet.Length < PacketBytes || frame.Length < FrameBytes)
            return false;

        packet.Slice(HeaderBytes, FrameBytes).CopyTo(frame);
        return true;
    }

    public static byte[] ExtractFrame(ReadOnlySpan<byte> packet)
    {
        byte[] frame = new byte[FrameBytes];
        if (!TryExtractFrame(packet, frame))
            throw new ArgumentException("The NXDN packet does not contain a complete 48-byte voice frame.", nameof(packet));
        return frame;
    }
}
