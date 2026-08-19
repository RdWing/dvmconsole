using System.Buffers.Binary;
using System.Text;

namespace DvmConsole.Audio;

public sealed record OggOpusTagSet(string Vendor, IReadOnlyDictionary<string, string> Fields);

// Reads and updates the OpusTags page without decoding or re-encoding audio.
// The editor intentionally accepts only a standalone, non-continuation tags
// page; this is the layout produced by the TAR encoder and avoids touching
// unfamiliar Ogg multiplexing arrangements.
public static class OggOpusTags
{
    private const int FixedHeaderLength = 27;
    private const uint OggCrcPolynomial = 0x04C11DB7;
    private static readonly byte[] OggCapturePattern = "OggS"u8.ToArray();
    private static readonly byte[] OpusTagsSignature = "OpusTags"u8.ToArray();

    public static OggOpusTagSet Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        while (TryReadPage(stream, out OggPage page))
        {
            if (page.Payload.AsSpan().StartsWith(OpusTagsSignature))
                return ParseTagPayload(page.Payload);
        }

        throw new InvalidDataException("The Ogg Opus file does not contain an OpusTags page.");
    }

    public static void Set(string path, string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string fullPath = Path.GetFullPath(path);
        string temporaryPath = $"{fullPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        DateTime originalWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
        bool replaced = false;
        try
        {
            using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                while (TryReadPage(input, out OggPage page))
                {
                    if (!replaced && page.Payload.AsSpan().StartsWith(OpusTagsSignature))
                    {
                        ValidateEditableTagPage(page);
                        OggOpusTagSet existing = ParseTagPayload(page.Payload);
                        var fields = new Dictionary<string, string>(existing.Fields, StringComparer.OrdinalIgnoreCase)
                        {
                            [name] = value
                        };
                        byte[] payload = BuildTagPayload(existing.Vendor, fields);
                        WriteReplacementPage(output, page, payload);
                        replaced = true;
                    }
                    else
                    {
                        output.Write(page.Header);
                        output.Write(page.Payload);
                    }
                }

                output.Flush(flushToDisk: true);
            }

            if (!replaced)
                throw new InvalidDataException("The Ogg Opus file does not contain an editable OpusTags page.");

            File.Move(temporaryPath, fullPath, overwrite: true);
            File.SetLastWriteTimeUtc(fullPath, originalWriteTimeUtc);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static bool TryReadPage(Stream stream, out OggPage page)
    {
        page = null!;
        byte[] fixedHeader = new byte[FixedHeaderLength];
        int firstByte = stream.ReadByte();
        if (firstByte < 0)
            return false;
        fixedHeader[0] = (byte)firstByte;
        stream.ReadExactly(fixedHeader.AsSpan(1));

        if (!fixedHeader.AsSpan(0, OggCapturePattern.Length).SequenceEqual(OggCapturePattern) ||
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

        byte[] header = new byte[FixedHeaderLength + segmentCount];
        fixedHeader.CopyTo(header, 0);
        segmentTable.CopyTo(header, FixedHeaderLength);
        page = new OggPage(header, segmentTable, payload);
        return true;
    }

    private static OggOpusTagSet ParseTagPayload(byte[] payload)
    {
        ReadOnlySpan<byte> data = payload;
        if (!data.StartsWith(OpusTagsSignature))
            throw new InvalidDataException("The Ogg page is not an OpusTags packet.");

        int cursor = OpusTagsSignature.Length;
        string vendor = ReadString(data, ref cursor);
        int fieldCount = ReadInt32(data, ref cursor);
        if (fieldCount < 0 || fieldCount > 4096)
            throw new InvalidDataException("The OpusTags field count is invalid.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < fieldCount; index++)
        {
            string field = ReadString(data, ref cursor);
            int separator = field.IndexOf('=');
            if (separator <= 0)
                continue;
            fields[field[..separator]] = field[(separator + 1)..];
        }

        return new OggOpusTagSet(vendor, fields);
    }

    private static byte[] BuildTagPayload(string vendor, IReadOnlyDictionary<string, string> fields)
    {
        using var output = new MemoryStream();
        output.Write(OpusTagsSignature);
        WriteString(output, vendor);
        WriteInt32(output, fields.Count);
        foreach ((string name, string value) in fields)
            WriteString(output, $"{name}={value}");
        return output.ToArray();
    }

    private static void ValidateEditableTagPage(OggPage page)
    {
        bool isContinuation = (page.Header[5] & 0x01) != 0;
        int completedPackets = page.SegmentTable.Count(length => length < byte.MaxValue);
        if (isContinuation || completedPackets != 1 || page.SegmentTable.Length == 0 ||
            page.SegmentTable[^1] == byte.MaxValue)
        {
            throw new InvalidDataException(
                "The OpusTags packet spans pages or shares a page and cannot be migrated safely.");
        }
    }

    private static void WriteReplacementPage(Stream output, OggPage original, byte[] payload)
    {
        List<byte> lacingValues = CreateLacingValues(payload.Length);
        if (lacingValues.Count > byte.MaxValue)
            throw new InvalidDataException("The embedded TAR metadata is too large for one Ogg page.");

        byte[] page = new byte[FixedHeaderLength + lacingValues.Count + payload.Length];
        original.Header.AsSpan(0, FixedHeaderLength).CopyTo(page);
        page[26] = (byte)lacingValues.Count;
        lacingValues.CopyTo(page, FixedHeaderLength);
        payload.CopyTo(page, FixedHeaderLength + lacingValues.Count);
        page.AsSpan(22, sizeof(uint)).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), CalculateCrc(page));
        output.Write(page);
    }

    private static List<byte> CreateLacingValues(int payloadLength)
    {
        var values = new List<byte>();
        int remaining = payloadLength;
        while (remaining >= byte.MaxValue)
        {
            values.Add(byte.MaxValue);
            remaining -= byte.MaxValue;
        }
        values.Add((byte)remaining);
        return values;
    }

    private static uint CalculateCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte value in data)
        {
            crc ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ OggCrcPolynomial : crc << 1;
        }
        return crc;
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int cursor)
    {
        int length = ReadInt32(data, ref cursor);
        if (length < 0 || cursor > data.Length - length)
            throw new InvalidDataException("The OpusTags string length is invalid.");
        string value = Encoding.UTF8.GetString(data.Slice(cursor, length));
        cursor += length;
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int cursor)
    {
        if (cursor > data.Length - sizeof(int))
            throw new InvalidDataException("The OpusTags packet is truncated.");
        int value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(cursor, sizeof(int)));
        cursor += sizeof(int);
        return value;
    }

    private static void WriteString(Stream output, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteInt32(output, bytes.Length);
        output.Write(bytes);
    }

    private static void WriteInt32(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not mask the migration failure that caused it.
        }
    }

    private sealed record OggPage(byte[] Header, byte[] SegmentTable, byte[] Payload);
}
