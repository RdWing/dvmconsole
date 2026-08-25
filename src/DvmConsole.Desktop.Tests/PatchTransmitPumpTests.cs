using System.Collections.Concurrent;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PatchTransmitPumpTests
{
    [Fact]
    public async Task EmptyPatchSourceDoesNotCreateAnOutboundCall()
    {
        var sent = new ConcurrentQueue<byte[]>();
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("DMR", "FNE", "dmr", 99, slot: 0),
            sourceId: 890,
            streamId: 101,
            new FakeVocoderSession(),
            (payload, _, _) => sent.Enqueue(payload.ToArray()));
        var pump = new PatchTransmitPump(session);

        pump.Complete();
        Assert.False(await pump.Started);
        await pump.Completion;

        Assert.Empty(sent);
        Assert.Null(pump.Failure);
    }

    [Fact]
    public async Task ReleasesOnePcmFramePerCadenceBeforeBuildingDmrPacket()
    {
        var sent = new ConcurrentQueue<byte[]>();
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("DMR", "FNE", "dmr", 99, slot: 0),
            sourceId: 890,
            streamId: 101,
            new FakeVocoderSession(),
            (payload, _, _) => sent.Enqueue(payload.ToArray()));
        var cadence = new ManualCadence();
        var pump = new PatchTransmitPump(session, waitForNextFrame: cadence.WaitAsync);

        Assert.True(pump.Enqueue(new short[VocoderFrameSizes.PcmSamplesPerFrame * 3]));
        await WaitUntilAsync(() => cadence.WaitCount == 1);
        Assert.Single(sent);

        cadence.Release();
        await WaitUntilAsync(() => cadence.WaitCount == 2);
        Assert.Single(sent);

        cadence.Release();
        await WaitUntilAsync(() => cadence.WaitCount == 3);
        Assert.Single(sent);

        cadence.Release();
        await WaitUntilAsync(() => sent.Count == 2);
        pump.Complete();
        await pump.Completion;

        Assert.Null(pump.Failure);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    private sealed class ManualCadence
    {
        private readonly SemaphoreSlim releases = new(0);
        private int waitCount;

        public int WaitCount => Volatile.Read(ref waitCount);

        public async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref waitCount);
            await releases.WaitAsync(cancellationToken);
        }

        public void Release() => releases.Release();
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Fill(0x5A);
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public int FlushEncode(Span<byte> codeword) => 0;
        public void Dispose() { }
    }
}
