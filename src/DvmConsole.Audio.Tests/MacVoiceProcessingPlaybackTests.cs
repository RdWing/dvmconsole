using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class MacVoiceProcessingPlaybackTests
{
    [Fact]
    public async Task FailsWhenTheNativeOutputStopsAcceptingSamples()
    {
        var session = new NoProgressVoiceSession();
        await using var playback = new MacVoiceProcessingPlayback(
            session,
            PcmAudioFormat.Voice8KhzMono16Bit,
            writeNoProgressTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<IOException>(async () =>
            await playback.WriteAsync(new short[160]));

        Assert.Equal(1, session.StartCalls);
        Assert.True(session.WriteCalls > 1);
    }

    private sealed class NoProgressVoiceSession : IVoiceProcessingPlaybackSession
    {
        public int QueuedSamples => 0;
        public TimeSpan StarvedDuration => TimeSpan.Zero;
        public TimeSpan PendingStarvedDuration => TimeSpan.Zero;
        public long OutputCallbackCount => 0;
        public int StartCalls { get; private set; }
        public int WriteCalls { get; private set; }

        public void StartPlayback() => StartCalls++;
        public void StopPlayback()
        {
        }

        public int Write(short[] samples)
        {
            WriteCalls++;
            return 0;
        }

        public void EndExpectedPlayback()
        {
        }
    }
}
