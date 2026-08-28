using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class OpusOggPcmStreamReaderTests
{
    [Fact]
    public async Task CancellationInterruptsABlockedSynchronousPacketRead()
    {
        using var source = new DisposalSignalingStream();
        using var releaseRead = new ManualResetEventSlim();
        using var packetReader = new BlockingPacketReader(releaseRead);
        var reader = new OpusOggPcmStreamReader(source, packetReader);
        using var cancellation = new CancellationTokenSource();

        try
        {
            Task<int> read = reader.ReadSamplesAsync(
                new short[160],
                cancellation.Token).AsTask();
            Assert.True(packetReader.ReadStarted.Wait(TimeSpan.FromSeconds(2)));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read).WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert.True(source.Disposed.IsSet);
            Assert.False(releaseRead.IsSet);
        }
        finally
        {
            releaseRead.Set();
            await reader.DisposeAsync();
        }
    }

    private sealed class BlockingPacketReader(ManualResetEventSlim releaseRead)
        : IOpusOggPacketReader
    {
        public ManualResetEventSlim ReadStarted { get; } = new();

        public bool HasNextPacket
        {
            get
            {
                ReadStarted.Set();
                releaseRead.Wait();
                throw new ObjectDisposedException(nameof(DisposalSignalingStream));
            }
        }

        public short[]? DecodeNextPacket()
            => throw new InvalidOperationException("No packet should be decoded.");

        public void Dispose()
            => ReadStarted.Dispose();
    }

    private sealed class DisposalSignalingStream : MemoryStream
    {
        public ManualResetEventSlim Disposed { get; } = new();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Disposed.Set();
            base.Dispose(disposing);
        }
    }
}
