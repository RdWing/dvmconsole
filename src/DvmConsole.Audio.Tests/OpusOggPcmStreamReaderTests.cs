// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class OpusOggPcmStreamReaderTests
{
    [Fact]
    public async Task DisposalJoinsCancelledDecodeAndRejectsNewReaders()
    {
        using var source = new DisposalSignalingStream();
        using var releaseRead = new ManualResetEventSlim();
        using var packetReader = new BlockingPacketReader(releaseRead);
        var reader = new OpusOggPcmStreamReader(source, packetReader);
        using var cancellation = new CancellationTokenSource();
        Task? disposal = null;
        try
        {
            Task<int> read = reader.ReadSamplesAsync(new short[160], cancellation.Token).AsTask();
            Assert.True(packetReader.ReadStarted.Wait(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadSamplesAsync(new short[160]).AsTask());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read).WaitAsync(TimeSpan.FromSeconds(10));
            disposal = reader.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, packetReader.DisposeCount);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadSamplesAsync(new short[160]).AsTask());
            releaseRead.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            await reader.DisposeAsync();
            Assert.Equal(1, packetReader.DisposeCount);
        }
        finally
        {
            releaseRead.Set();
            await (disposal ?? reader.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

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
        public int DisposeCount { get; private set; }

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
        {
            DisposeCount++;
            ReadStarted.Dispose();
        }
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
