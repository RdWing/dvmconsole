using fnecore;
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
}
