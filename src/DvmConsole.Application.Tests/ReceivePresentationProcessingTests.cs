// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ReceivePresentationProcessingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RecordingObserverGetsRawSamplesBeforeLocalProcessing(int writeKind)
    {
        var output = new Playback();
        var processor = new Processor();
        short[] recorded = new short[160];
        short[] input = Enumerable.Repeat((short)100, 160).ToArray();
        await using var playback = new ObservedAudioPlayback(output, samples => samples.CopyTo(recorded), processor);
        await Write(playback, input, writeKind);
        Assert.All(recorded, sample => Assert.Equal(100, sample));
        Assert.All(input, sample => Assert.Equal(100, sample));
        Assert.All(output.Samples, sample => Assert.Equal(200, sample));
        Assert.Equal(1, processor.Calls);
        output.LivePlaybackEnabled = false;
        await Write(playback, input, writeKind);
        Assert.All(recorded, sample => Assert.Equal(100, sample));
        Assert.Equal(1, processor.Calls);
    }

    [Fact]
    public async Task SteadyStatePresentationReusesItsBuffer()
    {
        var playback = new ObservedAudioPlayback(new Playback(), _ => { }, new Processor());
        short[] samples = new short[160];
        for (int i = 0; i < 100; i++)
            await playback.WriteAsync(samples);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            await playback.WriteAsync(samples);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        await playback.DisposeAsync();
    }

    private static ValueTask Write(ObservedAudioPlayback playback, short[] samples, int kind)
        => kind switch
        {
            1 => playback.WriteConcealmentAsync(samples),
            2 => playback.WriteLivePacketAsync(samples),
            _ => playback.WriteAsync(samples)
        };

    private sealed class Processor : IReceiveAudioProcessingSession
    {
        public bool HasReceiveAudioProcessing => true;
        public int Calls { get; private set; }
        public void DeferReceiveAudioProcessing() { }
        public void ProcessReceiveAudio(Span<short> samples)
        {
            Calls++;
            for (int i = 0; i < samples.Length; i++) samples[i] *= 2;
        }
    }

    private sealed class Playback : IAudioPlayback, ILiveAudioPlaybackControl
    {
        public short[] Samples { get; } = new short[160];
        public bool LivePlaybackEnabled { get; set; } = true;
        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            samples.CopyTo(Samples);
            return ValueTask.CompletedTask;
        }
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
