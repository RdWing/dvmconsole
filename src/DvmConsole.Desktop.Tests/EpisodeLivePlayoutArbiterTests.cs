using DvmConsole.Audio;
using DvmConsole.Desktop;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class EpisodeLivePlayoutArbiterTests
{
    [Fact]
    public async Task NewestProducerWithDecodedAudioRetiresItsPredecessor()
    {
        var playback = new RecordingPlayback();
        var arbiter = new EpisodeLivePlayoutArbiter(playback);
        EpisodeLivePlayoutArbiter.Producer first = arbiter.Register(41);
        EpisodeLivePlayoutArbiter.Producer replacement = arbiter.Register(42);

        await first.WritePacketAsync(Samples(1));
        await replacement.WritePacketAsync(Samples(2));
        await first.WritePacketAsync(Samples(3));

        Assert.Equal(new short[] { 1, 2 }, playback.Writes.Select(samples => samples[0]));
        Assert.Equal(1, playback.InputBoundaries);
        Assert.Equal(
            new EpisodeLivePlayoutDiagnostics(1, SamplesPerWrite),
            arbiter.GetDiagnostics());
    }

    [Fact]
    public async Task HeaderOnlyReplacementDoesNotSilenceTheActiveProducer()
    {
        var playback = new RecordingPlayback();
        var arbiter = new EpisodeLivePlayoutArbiter(playback);
        EpisodeLivePlayoutArbiter.Producer first = arbiter.Register(41);
        _ = arbiter.Register(42);

        await first.WritePacketAsync(Samples(1));
        await first.WritePacketAsync(Samples(2));

        Assert.Equal(new short[] { 1, 2 }, playback.Writes.Select(samples => samples[0]));
        Assert.Equal(default, arbiter.GetDiagnostics());
    }

    [Fact]
    public async Task RetiredProducerCannotRetakeAnIdleEpisodeLane()
    {
        var playback = new RecordingPlayback();
        var arbiter = new EpisodeLivePlayoutArbiter(playback);
        EpisodeLivePlayoutArbiter.Producer first = arbiter.Register(41);
        EpisodeLivePlayoutArbiter.Producer replacement = arbiter.Register(42);

        await first.WritePacketAsync(Samples(1));
        await replacement.WritePacketAsync(Samples(2));
        replacement.Release();
        await first.WritePacketAsync(Samples(3));

        Assert.Equal(new short[] { 1, 2 }, playback.Writes.Select(samples => samples[0]));
        Assert.Equal(
            new EpisodeLivePlayoutDiagnostics(1, SamplesPerWrite),
            arbiter.GetDiagnostics());
    }

    private const int SamplesPerWrite = 160;

    private static short[] Samples(short value)
    {
        var samples = new short[SamplesPerWrite];
        Array.Fill(samples, value);
        return samples;
    }

    private sealed class RecordingPlayback :
        IAudioPlayback,
        ILivePacketAudioPlayback,
        IConcealmentAudioPlayback,
        IAudioPlaybackBoundaryControl
    {
        public List<short[]> Writes { get; } = [];
        public int InputBoundaries { get; private set; }
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteLivePacketAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => WriteAsync(samples, cancellationToken);

        public ValueTask WriteConcealmentAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => WriteAsync(samples, cancellationToken);

        public void MarkInputBoundary() => InputBoundaries++;

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
