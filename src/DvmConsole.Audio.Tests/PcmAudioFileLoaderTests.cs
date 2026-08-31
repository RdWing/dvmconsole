using System.Buffers.Binary;
using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class PcmAudioFileLoaderTests
{
    [Fact]
    public async Task LoadsPcmWavAsEightKhzMonoSamples()
    {
        using var source = new MemoryStream(CreateWav(8000, [1000, -2000, 3000]));

        short[] samples = await PcmAudioFileLoader.LoadAsync(source);

        Assert.Equal([1000, -2000, 3000], samples);
    }

    [Fact]
    public async Task RejectsAlertAudioLongerThanConfiguredLimit()
    {
        using var source = new MemoryStream(CreateWav(
            8000,
            Enumerable.Repeat((short)1, 801).ToArray()));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PcmAudioFileLoader.LoadAsync(source, TimeSpan.FromMilliseconds(100)));
    }

    private static byte[] CreateWav(int sampleRate, IReadOnlyList<short> samples)
    {
        byte[] data = new byte[samples.Count * 2];
        for (int index = 0; index < samples.Count; index++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(index * 2, 2), samples[index]);

        using var output = new MemoryStream();
        output.Write("RIFF"u8);
        WriteUInt32(output, (uint)(36 + data.Length));
        output.Write("WAVE"u8);
        output.Write("fmt "u8);
        WriteUInt32(output, 16);
        WriteUInt16(output, 1);
        WriteUInt16(output, 1);
        WriteUInt32(output, (uint)sampleRate);
        WriteUInt32(output, (uint)(sampleRate * 2));
        WriteUInt16(output, 2);
        WriteUInt16(output, 16);
        output.Write("data"u8);
        WriteUInt32(output, (uint)data.Length);
        output.Write(data);
        return output.ToArray();
    }

    private static void WriteUInt16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }
}
