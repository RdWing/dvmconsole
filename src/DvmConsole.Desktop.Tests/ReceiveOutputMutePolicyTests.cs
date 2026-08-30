using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveOutputMutePolicyTests
{
    [Fact]
    public async Task SystemAndZoneScopesComposeWithoutUnmutingEachOther()
    {
        var alpha = Channel("Alpha", "Dispatch");
        var bravo = Channel("Alpha", "Tac");
        var dispatch = new ZoneViewModel("Dispatch", [alpha], []);
        var system = new SystemViewModel(
            new FneConnectionOptions("Alpha", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Alpha",
            "127.0.0.1:62031",
            [alpha, bravo],
            [dispatch]);
        var policy = new ReceiveOutputMutePolicy();

        try
        {
            Assert.True(policy.Toggle(dispatch));
            Assert.True(policy.IsMuted(alpha));
            Assert.False(policy.IsMuted(bravo));

            Assert.True(policy.Toggle(system));
            Assert.True(policy.IsMuted(alpha));
            Assert.True(policy.IsMuted(bravo));

            Assert.False(policy.Toggle(system));
            Assert.True(policy.IsMuted(alpha));
            Assert.False(policy.IsMuted(bravo));

            Assert.False(policy.Toggle(dispatch));
            Assert.False(policy.IsMuted(alpha));
        }
        finally
        {
            await system.DisposeAsync();
        }
    }

    [Fact]
    public async Task TransmitRestoreCannotBypassAnOperatorMuteScope()
    {
        var channel = Channel("Alpha", "Dispatch");
        var dispatch = new ZoneViewModel("Dispatch", [channel], []);
        var system = new SystemViewModel(
            new FneConnectionOptions("Alpha", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Alpha",
            "127.0.0.1:62031",
            [channel],
            [dispatch]);
        var policy = new ReceiveOutputMutePolicy();

        try
        {
            channel.SetAudioEnabled(true);
            channel.SetAudioSuspended(true);

            Assert.False(policy.ShouldEnableLivePlayback(
                channel,
                isTemporarilySuspended: true));
            Assert.True(policy.ShouldEnableLivePlayback(
                channel,
                isTemporarilySuspended: false));

            policy.Toggle(system);

            Assert.False(policy.ShouldEnableLivePlayback(
                channel,
                isTemporarilySuspended: false));
        }
        finally
        {
            await system.DisposeAsync();
        }
    }

    [Fact]
    public async Task MutedRecordingChannelStillRequiresDecodeButNotLivePlayback()
    {
        var channel = Channel("Alpha", "Dispatch");
        var system = new SystemViewModel(
            new FneConnectionOptions("Alpha", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Alpha",
            "127.0.0.1:62031",
            [channel]);
        var policy = new ReceiveOutputMutePolicy();

        try
        {
            channel.SetAudioEnabled(true);
            channel.SetRecordingEnabled(true);
            policy.Toggle(system);

            Assert.True(channel.IsRecordingEnabled);
            Assert.False(policy.ShouldEnableLivePlayback(
                channel,
                isTemporarilySuspended: false));
        }
        finally
        {
            await system.DisposeAsync();
        }
    }

    private static ChannelViewModel Channel(string system, string name)
        => new(new ChannelConfiguration
        {
            Name = name,
            System = system,
            Tgid = name == "Dispatch" ? "100" : "101",
            Mode = "p25"
        });
}
