using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class PatchTransmitSessionTests
{
    [Fact]
    public void SelectsAnalogPatchLifecycleWithoutAocoder()
    {
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("Analog", "Beta", "analog", 200, 0),
            sourceId: 42,
            streamId: 77,
            vocoder: null,
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)));

        session.Start();
        Assert.Equal(1, session.Process(new short[160]));
        session.End();

        Assert.Equal(2, packets.Count);
        Assert.Equal(AnalogAudioFrameType.VoiceStart, (AnalogAudioFrameType)packets[0].Payload[15]);
        Assert.Equal(AnalogAudioFrameType.Terminator, (AnalogAudioFrameType)packets[1].Payload[15]);
    }

    [Fact]
    public void SelectsDmrPatchLifecycleAndRejectsUnsupportedTargets()
    {
        var packets = new List<byte[]>();
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("DMR", "Beta", "dmr", 200, 1),
            sourceId: 42,
            streamId: 77,
            vocoder: new FakeVocoderSession(),
            send: (payload, _, _) => packets.Add(payload.ToArray()));

        session.Start();
        Assert.Equal(1, session.Process(new short[480]));
        session.End();

        Assert.Equal(3, packets.Count);
        Assert.Throws<NotSupportedException>(() => new PatchTransmitSession(
            new ChannelRuntimeDefinition("NXDN", "Beta", "nxdn", 200, 0),
            42,
            78,
            null,
            (_, _, _) => { }));
        Assert.Throws<InvalidOperationException>(() => new PatchTransmitSession(
            new ChannelRuntimeDefinition("RX", "Beta", "analog", 201, 0, rxOnly: true),
            42,
            79,
            null,
            (_, _, _) => { }));
    }

    [Fact]
    public void RequiresStartBeforePatchAudio()
    {
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("P25", "Beta", "p25", 200, 0),
            42,
            77,
            new FakeVocoderSession(),
            (_, _, _) => { });

        Assert.Throws<InvalidOperationException>(() => session.Process(new short[160]));
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Clear();
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose()
        {
        }
    }
}
