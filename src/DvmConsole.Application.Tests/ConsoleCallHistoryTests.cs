using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleCallHistoryTests
{
    [Fact]
    public void TracksCallLifecycleByStableIdAndBoundsTheNewestEntries()
    {
        var history = new ConsoleCallHistory(maximumEntries: 2);
        ConsoleCallHistoryRecord first = CreateCall(1, DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
        ConsoleCallHistoryRecord second = CreateCall(2, first.StartedAt.AddSeconds(1));
        ConsoleCallHistoryRecord third = CreateCall(3, first.StartedAt.AddSeconds(2));

        history.Add(first);
        history.Add(second);
        history.Add(third);

        Assert.Equal([third.Id, second.Id], history.Snapshot.Select(call => call.Id));
        Assert.Null(history.FindActiveReceive(
            first.SystemName,
            first.Protocol,
            first.PrimaryStreamId));
        Assert.Equal(second.Id, history.FindActiveReceive(
            second.SystemName,
            second.Protocol,
            second.PrimaryStreamId));
    }

    [Fact]
    public void UpdatesStreamsEncryptionAndCompletionWithoutPresentationObjects()
    {
        var history = new ConsoleCallHistory();
        ConsoleCallHistoryRecord call = CreateCall(10, DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
        history.Add(call);

        Assert.True(history.ObserveStream(call.Id, 11));
        Assert.True(history.UpdateEncryption(
            call.Id,
            RecordingEncryptionDescriptor.Secure(0x80, 0x1234)));
        Assert.True(history.Complete(call.Id, call.StartedAt.AddSeconds(3)));

        ConsoleCallHistoryRecord updated = Assert.Single(history.Snapshot);
        Assert.Equal(new uint[] { 10, 11 }, updated.StreamIds);
        Assert.True(updated.Encryption.IsSecure);
        Assert.Equal((ushort)0x1234, updated.Encryption.KeyId);
        Assert.Equal(call.StartedAt.AddSeconds(3), updated.EndedAt);
        Assert.False(updated.IsActive);
    }

    private static ConsoleCallHistoryRecord CreateCall(uint streamId, DateTimeOffset startedAt)
        => new(
            CallId.New(),
            startedAt,
            null,
            SystemId.FromName("North"),
            "North",
            null,
            "Dispatch",
            RadioMediaProtocol.P25,
            1001,
            2001,
            streamId,
            [streamId],
            null,
            "Unit 1001",
            ConsoleCallDirection.Receive,
            RecordingEncryptionDescriptor.Clear,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
}
