// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class TransmitTargetResolverTests
{
    [Fact]
    public void CapturesIndependentToneSelectionsAndKeepsStartedTargetsStable()
    {
        var alpha = Channel("Alpha", 100);
        var beta = Channel("Beta", 200);
        var channels = new ConsoleTransmitChannelDirectory([alpha, beta]);
        var endpoint = new Endpoint();
        var resolver = new TransmitTargetResolver(channels, [endpoint]);
        alpha.Operator.SetPageSelected(true);
        beta.Operator.SetAlertSelected(true);
        var selected = channels.SelectToneChannels(ConsoleToneTargets.Page);
        Assert.Equal([alpha.Id], selected);
        Assert.Equal([beta.Id], channels.SelectToneChannels(ConsoleToneTargets.Alert));
        Assert.Empty(channels.SelectToneChannels(ConsoleToneTargets.Alert, [alpha.Id]));
        var captured = Assert.Single(resolver.Capture([.. selected, alpha.Id]));
        Assert.Same(endpoint, captured.System);
        alpha.Operator.SetPageSelected(false);
        beta.Operator.SetPageSelected(true);
        alpha.Operator.SetTransmitEncrypted(true);
        Assert.Equal(alpha.Id, captured.Channel.Id);
        Assert.False(captured.Channel.TransmitEncrypted);
        Assert.Equal([beta.Id], channels.SelectToneChannels(ConsoleToneTargets.Page));
        Assert.True(Assert.Single(resolver.Capture([alpha.Id])).Channel.TransmitEncrypted);
    }

    [Fact]
    public void MissingEndpointFailsBeforeAnyTransmission()
    {
        var channel = Channel("Alpha", 100);
        var resolver = new TransmitTargetResolver(new ConsoleTransmitChannelDirectory([channel]), []);
        Assert.Equal("The system 'Test' was not found.",
            Assert.Throws<InvalidOperationException>(() => resolver.Capture([channel.Id])).Message);
    }

    private static ConsoleChannelState Channel(string name, uint id)
        => new(new ChannelRuntimeDefinition(name, "Test", "p25", id, 0));

    private sealed class Endpoint : IRadioTrafficEndpoint
    {
        public string Name => "TEST";
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => [];
        public IReadOnlyCollection<ChannelId> ChannelIds => [];
        public bool IsConnected => true;
        public uint? SourceId => 42;
        public TargetAuthorityState GetTargetAuthority(RadioMediaProtocol protocol, uint destinationId, byte runtimeSlot)
            => TargetAuthorityState.Available;
        public uint CreateStreamId() => throw new InvalidOperationException("Selection must not create a call.");
        public void SendTraffic(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort packetSequence, uint streamId)
            => throw new InvalidOperationException("Selection must not transmit.");
    }
}
