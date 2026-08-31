using System.Buffers.Binary;

namespace DvmConsole.Audio;

// Replaces the frame-aligned end granule emitted by the Ogg writer with the
// exact PCM duration. Opus may pad its final codec frame, while players such as
// Quick Look use this granule to decide the recording's displayed end time.
internal static class OggOpusDurationFinalizer
{
    private const int FixedHeaderLength = 27;
    private const int OpusGranuleSampleRate = 48_000;
    private static readonly byte[] CapturePattern = "OggS"u8.ToArray();
    private static readonly byte[] OpusHeadSignature = "OpusHead"u8.ToArray();

    public static void SetExactPcmDuration(Stream stream, long pcmSampleCount, int pcmSampleRate)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
            throw new ArgumentException("The Ogg stream must support reading, writing, and seeking.", nameof(stream));
        if (pcmSampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(pcmSampleCount));
        if (pcmSampleRate <= 0 || OpusGranuleSampleRate % pcmSampleRate != 0)
            throw new ArgumentOutOfRangeException(nameof(pcmSampleRate));

        stream.Position = 0;
        ushort? preSkip = null;
        OggPage? finalPage = null;
        while (TryReadPage(stream, out OggPage page))
        {
            if (preSkip is null && page.Payload.AsSpan().StartsWith(OpusHeadSignature))
            {
                if (page.Payload.Length < 12)
                    throw new InvalidDataException("The Ogg Opus identification header is truncated.");
                preSkip = BinaryPrimitives.ReadUInt16LittleEndian(page.Payload.AsSpan(10));
            }

            finalPage = page;
        }

        if (preSkip is null || finalPage is null)
            throw new InvalidDataException("The recording is not a complete Ogg Opus stream.");
        if ((finalPage.Bytes[5] & 0x04) == 0)
            throw new InvalidDataException("The Ogg Opus stream does not end with a finalized page.");

        long durationGranules = checked(pcmSampleCount * (OpusGranuleSampleRate / pcmSampleRate));
        long exactFinalGranule = checked(preSkip.Value + durationGranules);
        long encodedFinalGranule = BinaryPrimitives.ReadInt64LittleEndian(finalPage.Bytes.AsSpan(6));
        if (encodedFinalGranule < exactFinalGranule)
        {
            throw new InvalidDataException(
                "The encoded Ogg Opus stream is shorter than its source PCM duration.");
        }

        BinaryPrimitives.WriteInt64LittleEndian(finalPage.Bytes.AsSpan(6), exactFinalGranule);
        finalPage.Bytes.AsSpan(22, sizeof(uint)).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(
            finalPage.Bytes.AsSpan(22),
            OggPageChecksum.Calculate(finalPage.Bytes));

        stream.Position = finalPage.Offset;
        stream.Write(finalPage.Bytes);
        stream.Position = stream.Length;
    }

    private static bool TryReadPage(Stream stream, out OggPage page)
    {
        page = null!;
        long offset = stream.Position;
        byte[] fixedHeader = new byte[FixedHeaderLength];
        int firstByte = stream.ReadByte();
        if (firstByte < 0)
            return false;
        fixedHeader[0] = (byte)firstByte;
        stream.ReadExactly(fixedHeader.AsSpan(1));

        if (!fixedHeader.AsSpan(0, CapturePattern.Length).SequenceEqual(CapturePattern) ||
            fixedHeader[4] != 0)
        {
            throw new InvalidDataException("The recording contains an invalid Ogg page header.");
        }

        int segmentCount = fixedHeader[26];
        byte[] segmentTable = new byte[segmentCount];
        stream.ReadExactly(segmentTable);
        int payloadLength = segmentTable.Sum(value => value);
        byte[] payload = new byte[payloadLength];
        stream.ReadExactly(payload);

        byte[] bytes = new byte[FixedHeaderLength + segmentCount + payloadLength];
        fixedHeader.CopyTo(bytes, 0);
        segmentTable.CopyTo(bytes, FixedHeaderLength);
        payload.CopyTo(bytes, FixedHeaderLength + segmentCount);
        page = new OggPage(offset, bytes, payload);
        return true;
    }

    private sealed record OggPage(long Offset, byte[] Bytes, byte[] Payload);
}
