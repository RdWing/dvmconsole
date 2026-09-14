// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleSubscriberCommandDispatcherTests
{
    private static readonly SystemId System = SystemId.FromName("Test");

    [Fact]
    public void MatchesCommandSystemAndSubscriberWithoutMutatingPublishedHistory()
    {
        var dispatcher = new ConsoleSubscriberCommandDispatcher(new Clock());
        var radio = new Radio();
        var sent = dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 42);
        var original = dispatcher.History;
        dispatcher.HistoryChanged += (_, _) => throw new InvalidOperationException("Detached observer");
        Assert.Null(dispatcher.Acknowledge(new(SystemId.FromName("Other"), ConsoleSubscriberCommand.Page, 42)));
        Assert.Null(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Inhibit, 42)));
        Assert.Null(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 43)));
        var acknowledged = dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 42));
        Assert.Equal(sent.Id, acknowledged!.Id);
        Assert.True(acknowledged.Submitted);
        Assert.Equal(ConsoleSubscriberAcknowledgementState.Received, acknowledged.Acknowledgement);
        Assert.Equal(ConsoleSubscriberAcknowledgementState.Pending, original[0].Acknowledgement);
        Assert.Null(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 42)));
        Assert.Equal(1, radio.Sends);
    }

    [Fact]
    public void SynchronousAcknowledgementSurvivesSendCompletionButFailedSendDoesNot()
    {
        var dispatcher = new ConsoleSubscriberCommandDispatcher(new Clock());
        var radio = new Radio { Send = (command, target) => dispatcher.Acknowledge(new(System, command, target)) };
        var sent = dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.RadioCheck, 42);
        Assert.True(sent.Submitted);
        Assert.Equal(ConsoleSubscriberAcknowledgementState.Received, sent.Acknowledgement);
        radio.Send = (command, target) =>
        {
            dispatcher.Acknowledge(new(System, command, target));
            throw new IOException("Send failed");
        };
        var failed = dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.RadioCheck, 43);
        Assert.False(failed.Submitted);
        Assert.Equal(ConsoleSubscriberAcknowledgementState.None, failed.Acknowledgement);
        Assert.Null(failed.AcknowledgedAt);
        Assert.Null(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.RadioCheck, 43)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedRequestsCannotMisattributeAnOldResponse(bool firstAcknowledged)
    {
        var clock = new Clock();
        var dispatcher = new ConsoleSubscriberCommandDispatcher(clock);
        var radio = new Radio();
        dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 42);
        if (firstAcknowledged) dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 42));
        var repeated = dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 42);
        Assert.True(repeated.Submitted);
        Assert.Equal(ConsoleSubscriberAcknowledgementState.Ambiguous, repeated.Acknowledgement);
        Assert.Null(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 42)));
        Assert.Equal(firstAcknowledged ? ConsoleSubscriberAcknowledgementState.Received : ConsoleSubscriberAcknowledgementState.Ambiguous,
            dispatcher.History[1].Acknowledgement);
        clock.UtcNow += ConsoleSubscriberCommandDispatcher.AcknowledgementWindow;
        dispatcher.Expire();
        Assert.Equal(ConsoleSubscriberAcknowledgementState.TimedOut, dispatcher.History[0].Acknowledgement);
        var fresh = dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 42);
        Assert.Equal(ConsoleSubscriberAcknowledgementState.Pending, fresh.Acknowledgement);
        Assert.NotNull(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 42)));
        Assert.Equal(3, radio.Sends);
    }

    [Fact]
    public void ExpiryDisconnectAndEvictionRemovePendingCorrelation()
    {
        var clock = new Clock();
        var dispatcher = new ConsoleSubscriberCommandDispatcher(clock, 2);
        var radio = new Radio();
        dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 42);
        clock.UtcNow += ConsoleSubscriberCommandDispatcher.AcknowledgementWindow;
        Assert.Null(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 42)));
        Assert.Equal(ConsoleSubscriberAcknowledgementState.TimedOut, dispatcher.History[0].Acknowledgement);
        dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 43);
        dispatcher.Interrupt(System);
        Assert.Equal(ConsoleSubscriberAcknowledgementState.Interrupted, dispatcher.History[0].Acknowledgement);
        Assert.Null(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 43)));
        dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 44);
        dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 45);
        dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 46);
        Assert.Equal(2, dispatcher.History.Count);
        Assert.Null(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 44)));
        Assert.NotNull(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 45)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvictedAttemptsCannotMakeLateRepliesConfirmRapidRepeats(bool acknowledged)
    {
        var clock = new Clock();
        var dispatcher = new ConsoleSubscriberCommandDispatcher(clock, 2);
        var radio = new Radio();
        dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 42);
        if (acknowledged) dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 42));
        dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 43);
        dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 44);
        Assert.DoesNotContain(dispatcher.History, entry => entry.DestinationId == 42);
        var repeated = dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 42);
        Assert.True(repeated.Submitted);
        Assert.Equal(ConsoleSubscriberAcknowledgementState.Ambiguous, repeated.Acknowledgement);
        Assert.Null(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 42)));
        clock.UtcNow += ConsoleSubscriberCommandDispatcher.AcknowledgementWindow;
        var fresh = dispatcher.Submit(System, radio, radio, ConsoleSubscriberCommand.Page, 42);
        Assert.Equal(ConsoleSubscriberAcknowledgementState.Pending, fresh.Acknowledgement);
        Assert.NotNull(dispatcher.Acknowledge(new(System, ConsoleSubscriberCommand.Page, 42)));
        Assert.Equal(2, dispatcher.History.Count);
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch; }
    private sealed class Radio : IRadioTrafficEndpoint, IRadioSubscriberCommandEndpoint
    {
        public int Sends;
        public Action<ConsoleSubscriberCommand, uint>? Send;
        public void SendSubscriberCommand(ConsoleSubscriberCommand command, uint destinationId) { Sends++; Send?.Invoke(command, destinationId); }
        public string Name => "Test";
        public bool IsConnected => true;
        public uint? SourceId => 890;
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => [];
        public IReadOnlyCollection<ChannelId> ChannelIds => [];
        public uint CreateStreamId() => 1;
        public TargetAuthorityState GetTargetAuthority(RadioMediaProtocol protocol, uint destinationId, byte runtimeSlot) => TargetAuthorityState.Available;
        public void SendTraffic(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort packetSequence, uint streamId) { }
    }
}
