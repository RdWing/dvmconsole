using System.Buffers.Binary;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class OggOpusTagsTests
{
    [Fact]
    public async Task WritesAndAtomicallyUpdatesTagsWithoutBreakingAudio()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-ogg-tags-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string wavPath = Path.Combine(root, "source.wav");
        string opusPath = Path.Combine(root, "recording.opus");

        try
        {
            WriteWaveFile(wavPath, Enumerable.Repeat((short)1200, 800).ToArray());
            await OpusRecordingEncoder.EncodeWaveFileAsync(
                wavPath,
                opusPath,
                new Dictionary<string, string> { ["ORIGINAL"] = "present" });
            short[] decodedBeforeUpdate = await DecodeAllSamplesAsync(opusPath);

            OggOpusTags.Set(opusPath, "DVMCONSOLE_METADATA", new string('x', 2048));

            OggOpusTagSet tags = OggOpusTags.Read(opusPath);
            Assert.Equal("present", tags.Fields["ORIGINAL"]);
            Assert.Equal(new string('x', 2048), tags.Fields["DVMCONSOLE_METADATA"]);

            short[] decodedAfterUpdate = await DecodeAllSamplesAsync(opusPath);
            Assert.NotEmpty(decodedAfterUpdate);
            Assert.Equal(decodedBeforeUpdate, decodedAfterUpdate);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<short[]> DecodeAllSamplesAsync(string path)
    {
        await using FileStream source = File.OpenRead(path);
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

    private static void WriteWaveFile(string path, IReadOnlyCollection<short> samples)
    {
        const int sampleRate = 8000;
        const short channels = 1;
        const short bitsPerSample = 16;
        byte[] data = new byte[samples.Count * sizeof(short)];
        int offset = 0;
        foreach (short sample in samples)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset), sample);
            offset += sizeof(short);
        }

        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output);
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
    }
}
