// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class ProtocolStartupPacingTests
{
    [Fact]
    public void NxdnUsesEightyMillisecondTransportOpportunityPerPacket()
        => Assert.Equal(TimeSpan.FromMilliseconds(80), NxdnTxCallSession.PacketInterval);

    [Fact]
    public async Task ClearDmrHoldsVoiceUntilPacketOpportunityAfterLinkControl()
    {
        var packets = new List<byte[]>();
        var cadence = new ManualCadence();
        using var call = new DmrTxCallSession(
            1,
            2,
            slot: 0,
            streamId: 3,
            new FakeVocoderSession(),
            (payload, _, _) => packets.Add(payload.ToArray()),
            cadence.WaitAsync);

        call.Start();
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 3]);

        await WaitUntilAsync(() => cadence.WaitCount == 1);
        Assert.Single(packets);
        Assert.Equal((byte)0x21, packets[0][15]);

        cadence.Release();
        await WaitUntilAsync(() => packets.Count == 2);
        Assert.Equal((byte)0x10, packets[1][15]);
    }

    [Fact]
    public async Task SecureDmrPacesLinkControlPrivacyIndicatorAndVoiceSeparately()
    {
        var packets = new List<byte[]>();
        var cadence = new ManualCadence();
        using var call = new DmrTxCallSession(
            1,
            2,
            slot: 0,
            streamId: 3,
            new FakeHalfRateSession(),
            (payload, _, _) => packets.Add(payload.ToArray()),
            cadence.WaitAsync,
            privacy: new DmrPrivacyOptions(
                DmrPrivacyAlgorithms.Aes256,
                keyId: 42,
                new byte[32],
                Convert.FromHexString("12345678")));

        call.Start();
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 3]);

        await WaitUntilAsync(() => cadence.WaitCount == 1);
        Assert.Single(packets);
        Assert.Equal((byte)0x21, packets[0][15]);

        cadence.Release();
        await WaitUntilAsync(() => packets.Count == 2);
        Assert.Equal((byte)0x20, packets[1][15]);
        await WaitUntilAsync(() => cadence.WaitCount == 2);

        cadence.Release();
        await WaitUntilAsync(() => packets.Count == 3);
        Assert.Equal((byte)0x10, packets[2][15]);
    }

    [Fact]
    public async Task P25PhaseOneHoldsFirstLduUntilAfterStartupTdu()
    {
        var packets = new List<byte[]>();
        var cadence = new ManualCadence();
        using var call = new P25TxCallSession(
            1,
            2,
            streamId: 3,
            new FakeVocoderSession(),
            (payload, _, _) => packets.Add(payload.ToArray()),
            cadence.WaitAsync);

        call.Start();
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 9]);

        await WaitUntilAsync(() => cadence.WaitCount == 1);
        Assert.Single(packets);
        Assert.Equal(P25DfsiFrameCodec.TduDuid, packets[0][22]);

        cadence.Release();
        await WaitUntilAsync(() => packets.Count == 2);
        Assert.Equal(P25DfsiFrameCodec.Ldu1Duid, packets[1][22]);
    }

    [Fact]
    public async Task NxdnHoldsFirstVoiceFrameUntilAfterFacchCallStart()
    {
        var packets = new List<byte[]>();
        var cadence = new ManualCadence();
        using var call = new NxdnTxCallSession(
            1,
            2,
            group: true,
            streamId: 3,
            new FakeVocoderSession(),
            (payload, _, _) => packets.Add(payload.ToArray()),
            cadence.WaitAsync);

        call.Start();
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 4]);

        await WaitUntilAsync(() => cadence.WaitCount == 1);
        Assert.Single(packets);
        Assert.True(NxdnVoicePacketCodec.TryExtractCallMetadata(packets[0], out _));

        cadence.Release();
        await WaitUntilAsync(() => packets.Count == 2);
        Assert.True(NxdnVoicePacketCodec.TryExtractAmbe(
            packets[1],
            new byte[NxdnVoicePacketCodec.AmbeBytes],
            out int count));
        Assert.Equal(NxdnVoicePacketCodec.CodewordsPerFrame, count);
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
        private int started;
        private int waitCount;

        public int WaitCount => Volatile.Read(ref waitCount);

        public async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref started, 1) == 0)
                return;
            Interlocked.Increment(ref waitCount);
            await releases.WaitAsync(cancellationToken);
        }

        public void Release() => releases.Release();
    }

    private class FakeVocoderSession : IVocoderSession
    {
        public virtual int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Clear();
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose() { }
    }

    private sealed class FakeHalfRateSession : FakeVocoderSession, IHalfRateVocoderSession
    {
        public int EncodeParameters(ReadOnlySpan<short> samples, Span<byte> parameters)
        {
            parameters.Clear();
            return parameters.Length;
        }

        public int FlushEncodeParameters(Span<byte> parameters) => 0;

        public int DecodeParameters(
            ReadOnlySpan<byte> parameters,
            Span<short> samples,
            uint correctedErrors = 0,
            bool lost = false) => 0;

        public int ExtractParameters(ReadOnlySpan<byte> codeword, Span<byte> parameters)
        {
            parameters.Clear();
            return parameters.Length;
        }

        public void BuildCodeword(ReadOnlySpan<byte> parameters, Span<byte> codeword)
            => codeword.Clear();
    }
}
