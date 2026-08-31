namespace DvmConsole.Audio;

// Calculates the checksum defined by the Ogg container specification. Keeping
// this mechanism separate lets focused metadata and duration editors share it.
internal static class OggPageChecksum
{
    private const uint Polynomial = 0x04C11DB7;

    public static uint Calculate(ReadOnlySpan<byte> page)
    {
        uint crc = 0;
        foreach (byte value in page)
        {
            crc ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ Polynomial : crc << 1;
        }

        return crc;
    }
}
