using fnecore;
using System.Net;
using Xunit;

namespace DvmConsole.Fne.Tests;

public sealed class FneTransportComponentTests
{
    [Fact]
    public void ReplayWindowRejectsDuplicatesWithinBound()
    {
        var window = new InboundReplayWindow();
        byte[] wire = [1, 2, 3];

        Assert.True(window.TryRemember(wire));
        Assert.False(window.TryRemember(wire));

        window.Clear();
        Assert.True(window.TryRemember(wire));
    }

    [Fact]
    public void ReplayWindowEvictsOldestFingerprintAtExactBound()
    {
        var window = new InboundReplayWindow();
        byte[] oldest = BitConverter.GetBytes(0);
        Assert.True(window.TryRemember(oldest));
        for (int value = 1; value <= InboundReplayWindow.MaximumEntries; value++)
            Assert.True(window.TryRemember(BitConverter.GetBytes(value)));

        Assert.True(window.TryRemember(oldest));
        Assert.False(window.TryRemember(oldest));
    }

    [Theory]
    [InlineData(FneTransportEncryptionMode.Auto, FneTransportEncryptionMode.Ecb)]
    [InlineData(FneTransportEncryptionMode.Ecb, FneTransportEncryptionMode.Ecb)]
    [InlineData(FneTransportEncryptionMode.Cbc, FneTransportEncryptionMode.Cbc)]
    public void SelectsLegacyInitialSendMode(
        FneTransportEncryptionMode configured,
        FneTransportEncryptionMode expected)
        => Assert.Equal(expected, FneTransportNegotiationState.InitialMode(configured));

    [Fact]
    public void AutoModeAlternatesEcbThenCbcBeforeNegotiation()
    {
        byte[] key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        byte[] message = Enumerable.Range(0, 48).Select(value => (byte)value).ToArray();
        var state = new FneTransportNegotiationState(FneTransportEncryptionMode.Auto);
        state.SetPresharedKey(key);

        byte[] first = state.WrapForSend(message);
        byte[] second = state.WrapForSend(message);

        Assert.True(FneTransportCryptoCodec.TryUnwrap(
            first,
            key,
            FneTransportEncryptionMode.Ecb,
            out byte[] firstPlaintext));
        Assert.True(FneTransportCryptoCodec.TryUnwrap(
            second,
            key,
            FneTransportEncryptionMode.Cbc,
            out byte[] secondPlaintext));
        Assert.Equal(message, firstPlaintext);
        Assert.Equal(message, secondPlaintext);
    }

    [Fact]
    public async Task RepeatedSessionLifetimesStopEveryCapturedUdpReceiver()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            var lifetime = new FneTransportLifetime();
            UdpReceiver traffic;
            UdpReceiver metadata;
            using (FneTransportSessionContext.Use(
                       FneTransportEncryptionMode.Auto,
                       new FneTransportObservers(null, null),
                       lifetime))
            {
                traffic = new UdpReceiver();
                metadata = new UdpReceiver();
            }

            traffic.Connect(new IPEndPoint(IPAddress.Loopback, 62031));
            metadata.Connect(new IPEndPoint(IPAddress.Loopback, 62032));
            Task<UdpFrame> trafficReceive = traffic.Receive();
            Task<UdpFrame> metadataReceive = metadata.Receive();

            lifetime.Dispose();

            UdpFrame[] stoppedFrames = await Task.WhenAll(trafficReceive, metadataReceive)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(lifetime.IsStopped);
            Assert.All(stoppedFrames, frame => Assert.Empty(frame.Message));
        }
    }

    [Fact]
    public void SessionLifetimeStopsReceiversCreatedAfterShutdown()
    {
        var lifetime = new FneTransportLifetime();
        lifetime.Dispose();

        using (FneTransportSessionContext.Use(
                   FneTransportEncryptionMode.Auto,
                   new FneTransportObservers(null, null),
                   lifetime))
        {
            var receiver = new UdpReceiver();
            receiver.Connect(new IPEndPoint(IPAddress.Loopback, 62031));
            receiver.Send(new UdpFrame
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 62031),
                Message = [1]
            });
        }
    }
}
