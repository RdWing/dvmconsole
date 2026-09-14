// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ChannelControlCommandTests
{
    [Fact]
    public async Task RetiredControlHandlersRejectLatePreferenceCompletions()
    {
        var channel = new ConsoleChannelState(new("Dispatch", "System", "p25", 100, 0));
        var directory = new ConsoleTransmitChannelDirectory([channel]);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stopping = false;
        bool reconciled = false;
        var transmit = new ChannelTransmitControlCommands(directory, directory.CanTransmit,
            (_, _, _) => new ValueTask(saved.Task), () => stopping);
        var recording = new ChannelRecordingCommands(_ => channel, _ => true,
            (_, _, _) => new ValueTask(saved.Task), update => update(), _ => { },
            (_, _, _) => { reconciled = true; return Task.CompletedTask; }, () => stopping);
        Task select = transmit.SetSelectedAsync(channel.Id, true).AsTask();
        Task record = recording.SetEnabledAsync(channel.Id, true).AsTask();
        stopping = true;
        saved.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => select);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => record);
        Assert.False(channel.Operator.Snapshot.TransmitSelected);
        Assert.False(channel.Operator.Snapshot.RecordingEnabled);
        Assert.False(reconciled);
        Assert.Throws<ObjectDisposedException>(() => transmit.SetToneSelected(channel.Id, true, ConsoleToneTargets.Page));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transmit.SetSelectionAsync([channel.Id], true).AsTask());
    }

    [Fact]
    public async Task BulkSelectionCommitsOnlyAfterAllPreferencesAndKeepsUnrelatedChannels()
    {
        var first = new ConsoleChannelState(new("First", "System", "p25", 100, 0));
        var second = new ConsoleChannelState(new("Second", "System", "p25", 101, 0));
        var outside = new ConsoleChannelState(new("Outside", "Other", "p25", 102, 0));
        outside.Operator.SetTransmitSelected(true);
        var directory = new ConsoleTransmitChannelDirectory([first, second, outside]);
        int saves = 0;
        bool fail = true;
        var commands = new ChannelTransmitControlCommands(directory, directory.CanTransmit, (id, _, _) =>
        {
            saves++;
            Assert.False(first.Operator.Snapshot.TransmitSelected);
            Assert.False(second.Operator.Snapshot.TransmitSelected);
            return fail && id == second.Id ? ValueTask.FromException(new IOException("Storage full")) : ValueTask.CompletedTask;
        });
        await Assert.ThrowsAsync<IOException>(() => commands.SetSelectionAsync([first.Id, second.Id], true).AsTask());
        Assert.False(first.Operator.Snapshot.TransmitSelected);
        fail = false;
        saves = 0;
        var result = await commands.SetSelectionAsync([first.Id, first.Id, second.Id], null);
        Assert.Equal((2, true), result);
        Assert.Equal(2, saves);
        Assert.True(first.Operator.Snapshot.TransmitSelected);
        Assert.True(second.Operator.Snapshot.TransmitSelected);
        Assert.True(outside.Operator.Snapshot.TransmitSelected);
    }

    [Fact]
    public async Task BulkSelectionChecksCancellationInsideTheCommitBoundary()
    {
        var channel = new ConsoleChannelState(new("Dispatch", "System", "p25", 100, 0));
        var directory = new ConsoleTransmitChannelDirectory([channel]);
        using var cancellation = new CancellationTokenSource();
        var commands = new ChannelTransmitControlCommands(directory, directory.CanTransmit,
            (_, _, _) => ValueTask.CompletedTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commands.SetSelectionAsync([channel.Id], true,
            applyState: update => { cancellation.Cancel(); update(); }, token: cancellation.Token).AsTask());
        Assert.False(channel.Operator.Snapshot.TransmitSelected);
    }

    [Fact]
    public async Task RejectedPreferencesDoNotCommitTransmitOrRecordingIntent()
    {
        var channel = new ConsoleChannelState(new("Dispatch", "System", "p25", 100, 0));
        var directory = new ConsoleTransmitChannelDirectory([channel]);
        var transmit = new ChannelTransmitControlCommands(directory, directory.CanTransmit,
            (_, _, _) => ValueTask.FromException(new IOException("Storage full")));
        bool reconciled = false;
        var recording = new ChannelRecordingCommands(_ => channel, _ => true,
            (_, _, _) => ValueTask.FromException(new IOException("Storage full")), action => action(), _ => { },
            (_, _, _) => { reconciled = true; return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => transmit.SetSelectedAsync(channel.Id, true).AsTask());
        await Assert.ThrowsAsync<IOException>(() => recording.SetEnabledAsync(channel.Id, true).AsTask());
        Assert.False(channel.Operator.Snapshot.TransmitSelected);
        Assert.False(channel.Operator.Snapshot.RecordingEnabled);
        Assert.False(reconciled);
    }

    [Fact]
    public async Task CancelledLatePreferenceCompletionDoesNotStartRecordingAudio()
    {
        var channel = new ConsoleChannelState(new("Dispatch", "System", "p25", 100, 0));
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        bool reconciled = false;
        var commands = new ChannelRecordingCommands(_ => channel, _ => true,
            (_, _, _) => new ValueTask(saved.Task), action => action(), _ => { },
            (_, _, _) => { reconciled = true; return Task.CompletedTask; });
        Task enable = commands.SetEnabledAsync(channel.Id, true, cancellation.Token).AsTask();
        Assert.False(channel.Operator.Snapshot.RecordingEnabled);
        cancellation.Cancel();
        saved.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enable);
        Assert.False(channel.Operator.Snapshot.RecordingEnabled);
        Assert.False(reconciled);
    }

    [Fact]
    public void ToneIntentRetainsHostAdmissionPolicyAndDoesNotWritePreferences()
    {
        var channel = new ConsoleChannelState(new("Dispatch", "System", "p25", 100, 0));
        channel.SetAuthority(TargetAuthorityState.Unavailable);
        var directory = new ConsoleTransmitChannelDirectory([channel]);
        ValueTask Save(ChannelId id, ChannelTransmitPreferenceChange change, CancellationToken token)
            => throw new InvalidOperationException("Tone intent must not be persisted");
        var results = new List<ChannelSelectionResult>();
        var desktop = new ChannelTransmitControlCommands(directory, directory.CanTransmit, Save,
            selectionChanged: results.Add);
        var mobile = new ChannelTransmitControlCommands(directory, directory.CanTransmitByConfiguration, Save,
            selectionChanged: results.Add);
        Assert.False(desktop.SetToneSelected(channel.Id, true, ConsoleToneTargets.Page));
        Assert.False(channel.Operator.Snapshot.PageSelected);
        Assert.True(mobile.SetToneSelected(channel.Id, true, ConsoleToneTargets.Page));
        Assert.True(channel.Operator.Snapshot.PageSelected);
        Assert.Equal(new ChannelSelectionResult(channel.Id, TransmitSelectionKind.Page, true, false), results[0]);
        Assert.Equal(new ChannelSelectionResult(channel.Id, TransmitSelectionKind.Page, true, true), results[1]);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => mobile.SetToneSelected(channel.Id, false,
            ConsoleToneTargets.Page, canceled.Token));
        Assert.True(channel.Operator.Snapshot.PageSelected);
        Assert.Equal(2, results.Count);
    }
}
