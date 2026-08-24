using DvmConsole.Audio;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PcmPlaybackPumpTests
{
    [Fact]
    public async Task WritesEveryChunkAndSignalsFirstOutputOnce()
    {
        var reader = new FakeReader(
            new short[] { 1, 2, 3 },
            Array.Empty<short>(),
            new short[] { 4, 5 });
        var playback = new FakePlayback();
        int firstOutputSignals = 0;

        bool wroteOutput = await PcmPlaybackPump.RunAsync(
            reader,
            playback,
            rateConverter: null,
            CancellationToken.None,
            () =>
            {
                firstOutputSignals++;
                return ValueTask.CompletedTask;
            });

        Assert.True(wroteOutput);
        Assert.Equal(1, firstOutputSignals);
        Assert.Equal(new short[] { 1, 2, 3 }, playback.Writes.Single());
    }

    [Fact]
    public async Task EmptyReaderProducesNoWritesOrSignal()
    {
        var reader = new FakeReader(Array.Empty<short>());
        var playback = new FakePlayback();
        int firstOutputSignals = 0;

        bool wroteOutput = await PcmPlaybackPump.RunAsync(
            reader,
            playback,
            rateConverter: null,
            CancellationToken.None,
            () =>
            {
                firstOutputSignals++;
                return ValueTask.CompletedTask;
            });

        Assert.False(wroteOutput);
        Assert.Empty(playback.Writes);
        Assert.Equal(0, firstOutputSignals);
    }

    [Fact]
    public async Task WaitsForPlaybackPacingBeforeWritingTheNextChunk()
    {
        var reader = new FakeReader(
            new short[] { 1, 2, 3 },
            new short[] { 4, 5 });
        var playback = new FakePlayback();
        var pacer = new BlockingSecondWritePacer();

        Task<bool> pump = PcmPlaybackPump.RunAsync(
            reader,
            playback,
            rateConverter: null,
            CancellationToken.None,
            pacer: pacer);

        await pacer.SecondWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Single(playback.Writes);
        Assert.Equal([3], pacer.ObservedSampleCounts);

        pacer.AllowSecondWrite.TrySetResult();
        Assert.True(await pump.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, playback.Writes.Count);
        Assert.Equal([3, 2], pacer.ObservedSampleCounts);
    }

    private sealed class FakeReader(params short[][] chunks) : IAudioPcmStreamReader
    {
        private readonly Queue<short[]> remaining = new(chunks);

        public int SampleRate => 8_000;

        public ValueTask<int> ReadSamplesAsync(
            Memory<short> destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!remaining.TryDequeue(out short[]? chunk))
                return ValueTask.FromResult(0);
            chunk.CopyTo(destination);
            return ValueTask.FromResult(chunk.Length);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public List<short[]> Writes { get; } = [];
        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSecondWritePacer : IPcmPlaybackPacer
    {
        private int waitCount;

        public TaskCompletionSource SecondWaitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSecondWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<int> ObservedSampleCounts { get; } = [];

        public async ValueTask WaitBeforeWriteAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref waitCount) != 2)
                return;

            SecondWaitStarted.TrySetResult();
            await AllowSecondWrite.Task.WaitAsync(cancellationToken);
        }

        public void ObserveWrittenSamples(int sampleCount)
            => ObservedSampleCounts.Add(sampleCount);
    }
}
