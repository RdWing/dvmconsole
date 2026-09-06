// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class NxdnTxCallSessionTests
{
    [Fact]
    public async Task FailedCompletionRetriesTheTerminatorWithoutRebuildingTheCallTail()
    {
        bool failNextPacket = false;
        int failedPackets = 0;
        var packets = new List<byte[]>();
        using var session = new NxdnTxCallSession(
            sourceId: 1,
            destinationId: 2,
            group: true,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (payload, _, _) =>
            {
                if (failNextPacket)
                {
                    failNextPacket = false;
                    failedPackets++;
                    throw new IOException("transient transport failure");
                }
                packets.Add(payload.ToArray());
            },
            waitForNextPacket: TestPacketCadence.NoDelayAsync);
        session.Start();
        failNextPacket = true;

        await Assert.ThrowsAsync<IOException>(() => session.EndAsync().AsTask());
        await session.EndAsync();

        Assert.Equal(1, failedPackets);
        Assert.True(session.IsEnded);
        Assert.NotEmpty(packets);
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
