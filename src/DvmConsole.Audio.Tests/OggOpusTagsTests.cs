using System.Buffers.Binary;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class OggOpusTagsTests
{
    [Fact]
    public async Task RangeEncodingUsesOnlyTheRequestedPcmDuration()
    {
        const int rangeSamples = 1_237;
        using MemoryStream wave = CreateWave(Enumerable.Repeat((short)1200, 4_000));
        using var opus = new MemoryStream();

        await OpusRecordingEncoder.EncodeWaveStreamRangeAsync(
            wave,
            opus,
            startSample: 911,
            sampleCount: rangeSamples);

        (ushort preSkip, long finalGranule) = ReadOpusTiming(opus.ToArray());

        Assert.Equal(preSkip + rangeSamples * 6L, finalGranule);
    }

    [Fact]
    public async Task RangeEncodingSupportsCallerOwnedStreams()
    {
        const int rangeSamples = 777;
        using MemoryStream wave = CreateWave(Enumerable.Repeat((short)900, 2_000));
        using var opus = new MemoryStream();

        await OpusRecordingEncoder.EncodeWaveStreamRangeAsync(
            wave,
            opus,
            startSample: 123,
            sampleCount: rangeSamples);

        Assert.True(wave.CanRead);
        Assert.True(opus.CanWrite);
        (ushort preSkip, long finalGranule) = ReadOpusTiming(opus.ToArray());
        Assert.Equal(preSkip + rangeSamples * 6L, finalGranule);
    }

    [Fact]
    public async Task FinalGranuleMatchesSourceDurationForPartialOpusFrame()
    {
        const int sourceSampleCount = 8_123;
        using MemoryStream wave = CreateWave(Enumerable.Repeat((short)1200, sourceSampleCount));
        using var opus = new MemoryStream();

        await OpusRecordingEncoder.EncodeWaveStreamAsync(wave, opus);

        (ushort preSkip, long finalGranule) = ReadOpusTiming(opus.ToArray());

        Assert.Equal(preSkip + sourceSampleCount * 6L, finalGranule);
    }

    [Fact]
    public async Task UpdatesTagsToACallerOwnedDestinationWithoutBreakingAudio()
    {
        using MemoryStream wave = CreateWave(Enumerable.Repeat((short)1200, 800));
        using var opus = new MemoryStream();
        await OpusRecordingEncoder.EncodeWaveStreamAsync(
            wave,
            opus,
            new Dictionary<string, string> { ["ORIGINAL"] = "present" });
        byte[] originalOpus = opus.ToArray();
        short[] decodedBeforeUpdate = await DecodeAllSamplesAsync(originalOpus);

        using var input = new MemoryStream(originalOpus);
        using var updated = new MemoryStream();
        OggOpusTags.Set(input, updated, "DVMCONSOLE_METADATA", new string('x', 2048));

        updated.Position = 0;
        OggOpusTagSet tags = OggOpusTags.Read(updated);
        Assert.Equal("present", tags.Fields["ORIGINAL"]);
        Assert.Equal(new string('x', 2048), tags.Fields["DVMCONSOLE_METADATA"]);

        short[] decodedAfterUpdate = await DecodeAllSamplesAsync(updated.ToArray());
        Assert.NotEmpty(decodedAfterUpdate);
        Assert.Equal(decodedBeforeUpdate, decodedAfterUpdate);
    }

    private static async Task<short[]> DecodeAllSamplesAsync(byte[] encoded)
    {
        await using var source = new MemoryStream(encoded);
        await using IAudioPcmStreamReader reader = await PcmStreamDecoder.OpenAsync(source);
        var decoded = new List<short>();
        short[] buffer = new short[1600];
        while (true)
        {
            int count = await reader.ReadSamplesAsync(buffer);
            if (count == 0)
                return decoded.ToArray();
            decoded.AddRange(buffer.AsSpan(0, count).ToArray());
        }
    }

    private static MemoryStream CreateWave(IEnumerable<short> sourceSamples)
    {
        short[] samples = sourceSamples.ToArray();
        const int sampleRate = 8000;
        const short channels = 1;
        const short bitsPerSample = 16;
        byte[] data = new byte[samples.Length * sizeof(short)];
        int offset = 0;
        foreach (short sample in samples)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset), sample);
            offset += sizeof(short);
        }

        var output = new MemoryStream();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + data.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();
        output.Position = 0;
        return output;
    }

    private static (ushort PreSkip, long FinalGranule) ReadOpusTiming(byte[] file)
    {
        ushort? preSkip = null;
        long finalGranule = -1;
        int offset = 0;
        while (offset < file.Length)
        {
            ReadOnlySpan<byte> page = file.AsSpan(offset);
            Assert.True(page.Length >= 27);
            Assert.True(page[..4].SequenceEqual("OggS"u8));
            int segmentCount = page[26];
            Assert.True(page.Length >= 27 + segmentCount);
            int bodyLength = 0;
            for (int index = 0; index < segmentCount; index++)
                bodyLength += page[27 + index];
            int pageLength = 27 + segmentCount + bodyLength;
            Assert.True(page.Length >= pageLength);

            ReadOnlySpan<byte> body = page.Slice(27 + segmentCount, bodyLength);
            if (preSkip is null && body.Length >= 12 && body[..8].SequenceEqual("OpusHead"u8))
                preSkip = BinaryPrimitives.ReadUInt16LittleEndian(body[10..]);
            finalGranule = BinaryPrimitives.ReadInt64LittleEndian(page[6..]);
            offset += pageLength;
        }

        Assert.NotNull(preSkip);
        Assert.True(finalGranule >= 0);
        return (preSkip.Value, finalGranule);
    }
}
