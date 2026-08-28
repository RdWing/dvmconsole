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

    [Fact]
    public void RejectsMissingPasswordBeforeConstructingThePeer()
    {
        var options = new FneConnectionOptions(
            "Test", "Console", "127.0.0.1", 62031, 1, null, false, null);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => FnePeerSessionFactory.ValidateSessionPrerequisites(options));

        Assert.Contains("requires a password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsEncryptedTransportWithoutAPresharedKey()
    {
        var options = new FneConnectionOptions(
            "Test", "Console", "127.0.0.1", 62031, 1, "password", true, null);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => FnePeerSessionFactory.ValidateSessionPrerequisites(options));

        Assert.Contains("requires a preshared key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsExplicitPlaintextAndEncryptedSessionPrerequisites()
    {
        FnePeerSessionFactory.ValidateSessionPrerequisites(new FneConnectionOptions(
            "Plaintext", "Console", "127.0.0.1", 62031, 1, "password", false, null));
        FnePeerSessionFactory.ValidateSessionPrerequisites(new FneConnectionOptions(
            "Encrypted", "Console", "127.0.0.1", 62031, 1, "password", true, "0011"));
    }
}
