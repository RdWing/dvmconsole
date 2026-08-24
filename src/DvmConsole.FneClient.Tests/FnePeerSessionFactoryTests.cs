using DvmConsole.FneClient;
using fnecore;
using System.Net;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class FnePeerSessionFactoryTests
{
    [Theory]
    [InlineData(FneTransportEncryptionPreference.Auto, FneTransportEncryptionMode.Auto)]
    [InlineData(FneTransportEncryptionPreference.Ecb, FneTransportEncryptionMode.Ecb)]
    [InlineData(FneTransportEncryptionPreference.Cbc, FneTransportEncryptionMode.Cbc)]
    public void MapsConfiguredTransportPreference(
        FneTransportEncryptionPreference preference,
        FneTransportEncryptionMode expected)
        => Assert.Equal(expected, FnePeerSessionFactory.ToTransportMode(preference));

    [Fact]
    public void PeerSessionStopIsIdempotentAndAlwaysStopsTransport()
    {
        var lifetime = new FneTransportLifetime();
        var peer = new FnePeer(
            "TEST",
            1,
            new IPEndPoint(IPAddress.Loopback, 62031));
        var session = new FnePeerSession(peer, lifetime);

        session.Stop();
        session.Stop();

        Assert.True(lifetime.IsStopped);
        Assert.False(peer.IsStarted);
    }
}
