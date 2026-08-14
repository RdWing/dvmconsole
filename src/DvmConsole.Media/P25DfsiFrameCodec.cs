using DvmConsole.FneClient;

namespace DvmConsole.Media;

/// <summary>
/// Extracts the nine 88-bit IMBE codewords from one complete P25 DFSI LDU.
/// The FNE payload contains a 24-byte network header followed by the DFSI
/// records whose lengths and codeword offsets are defined by the P25 wire
/// format.
/// </summary>
public static class P25DfsiFrameCodec
{
    public const int HeaderBytes = 24;
    public const int RecordLengthOffset = 23;
    public const int RecordBytes = 154;
    public const int CodewordBytes = 11;
    public const int CodewordsPerLdu = 9;
    public const int ImbeBytes = CodewordBytes * CodewordsPerLdu;

    private static readonly byte[] Ldu1RecordTypes = [0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A];
    private static readonly byte[] Ldu2RecordTypes = [0x6B, 0x6C, 0x6D, 0x6E, 0x6F, 0x70, 0x71, 0x72, 0x73];
    private static readonly int[] RecordLengths = [22, 14, 17, 17, 17, 17, 17, 17, 16];
    private static readonly int[] CodewordOffsets = [10, 1, 5, 5, 5, 5, 5, 5, 4];

    public static bool TryExtractImbe(FneTrafficFrame traffic, Span<byte> imbe)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.Protocol != FneTrafficProtocol.P25 ||
            !IsVoiceLdu(traffic) ||
            imbe.Length < ImbeBytes)
            return false;

        bool ldu1 = string.Equals(traffic.Subtype, "LDU1", StringComparison.OrdinalIgnoreCase);
        return TryExtractImbe(traffic.Payload, ldu1, imbe);
    }

    public static byte[] ExtractImbe(FneTrafficFrame traffic)
    {
        byte[] imbe = new byte[ImbeBytes];
        if (!TryExtractImbe(traffic, imbe))
            throw new ArgumentException("The P25 packet does not contain a complete voice LDU.", nameof(traffic));
        return imbe;
    }

    private static bool TryExtractImbe(ReadOnlySpan<byte> payload, bool ldu1, Span<byte> imbe)
    {
        if (payload.Length <= RecordLengthOffset ||
            payload[RecordLengthOffset] < HeaderBytes ||
            payload[RecordLengthOffset] > payload.Length)
            return false;

        int recordBytes = payload[RecordLengthOffset] - HeaderBytes;
        if (recordBytes < RecordBytes)
            return false;

        ReadOnlySpan<byte> records = payload.Slice(HeaderBytes, recordBytes);
        ReadOnlySpan<byte> expectedTypes = ldu1 ? Ldu1RecordTypes : Ldu2RecordTypes;
        int recordOffset = 0;
        int imbeOffset = 0;

        for (int index = 0; index < RecordLengths.Length; index++)
        {
            int recordLength = RecordLengths[index];
            if (records[recordOffset] != expectedTypes[index] ||
                recordLength <= CodewordOffsets[index] + CodewordBytes)
                return false;

            records.Slice(recordOffset + CodewordOffsets[index], CodewordBytes)
                .CopyTo(imbe[imbeOffset..]);
            recordOffset += recordLength;
            imbeOffset += CodewordBytes;
        }

        return true;
    }

    private static bool IsVoiceLdu(FneTrafficFrame traffic)
    {
        return string.Equals(traffic.FrameType, "VOICE", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(traffic.Subtype, "LDU1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(traffic.Subtype, "LDU2", StringComparison.OrdinalIgnoreCase));
    }
}
