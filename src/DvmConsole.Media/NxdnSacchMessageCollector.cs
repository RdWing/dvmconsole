namespace DvmConsole.Media;

// Reassembles the four 18-bit SACCH fragments carried by consecutive NXDN
// voice frames. Structure values count down from 3 to 0 on the wire.
internal sealed class NxdnSacchMessageCollector
{
    private const int FragmentBits = 18;
    private const int FragmentCount = 4;
    private readonly byte[] linkControl = new byte[9];
    private int nextPart;

    public bool TryAccept(
        ReadOnlySpan<byte> packet,
        out NxdnVoicePacketCodec.CallMetadata metadata)
    {
        metadata = default;
        Span<byte> fragment = stackalloc byte[3];
        if (!NxdnVoicePacketCodec.TryExtractSacchFragment(packet, out byte structure, fragment))
        {
            Reset();
            return false;
        }

        int part = 3 - structure;
        if (part == 0)
            Reset();
        if (part != nextPart)
        {
            Reset();
            return false;
        }

        for (int bit = 0; bit < FragmentBits; bit++)
            SetBit(linkControl, (part * FragmentBits) + bit, GetBit(fragment, bit));
        nextPart++;
        if (nextPart < FragmentCount)
            return false;

        bool parsed = NxdnVoicePacketCodec.TryParseCallMetadata(linkControl, out metadata);
        Reset();
        return parsed;
    }

    public void Reset()
    {
        Array.Clear(linkControl);
        nextPart = 0;
    }

    private static bool GetBit(ReadOnlySpan<byte> data, int bit)
        => (data[bit / 8] & (0x80 >> (bit % 8))) != 0;

    private static void SetBit(Span<byte> data, int bit, bool value)
    {
        byte mask = (byte)(0x80 >> (bit % 8));
        if (value)
            data[bit / 8] |= mask;
        else
            data[bit / 8] &= (byte)~mask;
    }
}
