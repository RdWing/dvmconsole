// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ChannelStatePresentationTests
{
    [Fact]
    public void SharedUpdatesCoalesceOnUiAndRetirementDropsPendingNotifications()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        { Name = "Dispatch", System = "First", Mode = "p25", Tgid = "100" });
        var dispatcher = new DeferredDispatcher();
        channel.ConfigureStatePresentation(dispatcher);
        var changes = new List<string?>();
        int persistenceRequests = 0;
        channel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        channel.VolumeChanged += (_, _) => persistenceRequests++;
        channel.RecordingStateChanged += (_, _) => persistenceRequests++;
        var state = channel.SessionState;
        for (int index = 1; index <= 100; index++) state.Operator.SetGain(index / 200.0);
        state.Operator.SetBalance(-0.5);
        state.Operator.SetOutputRoute("saved-output");
        state.Operator.SetRecordingEnabled(true);
        state.Operator.SetTransmitSelected(true);
        state.Operator.SetPageSelected(true);
        state.Operator.SetAlertSelected(true);
        state.Operator.SetAudioEnabled(true);
        state.Runtime.MarkReceiving(42, 77);
        state.SetAuthority(TargetAuthorityState.Available);
        Assert.Empty(changes);
        Assert.Equal(1, dispatcher.PendingCount);
        Assert.Equal(0, persistenceRequests);
        dispatcher.RunPending();
        Assert.Equal(0.5, channel.Volume);
        Assert.Equal(-0.5, channel.StereoBalance);
        Assert.Contains(nameof(channel.Volume), changes);
        Assert.Contains(nameof(channel.IsRecordingEnabled), changes);
        Assert.Contains(nameof(channel.IsTransmitSelected), changes);
        Assert.Contains(nameof(channel.IsPageSelected), changes);
        Assert.Contains(nameof(channel.IsAlertSelected), changes);
        Assert.Contains(nameof(channel.IsReceivePresentationActive), changes);
        Assert.Contains(nameof(channel.LastCallerText), changes);
        Assert.Contains(nameof(channel.TalkgroupAvailability), changes);
        changes.Clear();
        state.Operator.SetGain(0.75);
        state.Runtime.MarkIdle();
        channel.DetachSessionState();
        dispatcher.RunPending();
        state.Operator.SetGain(0.5);
        state.Runtime.MarkReceiving(43, 78);
        Assert.Empty(changes);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    private sealed class DeferredDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> pending = new();
        public int PendingCount => pending.Count;
        public bool CheckAccess() => false;
        public void Post(Action action, bool background = false) => pending.Enqueue(action);
        public ValueTask InvokeAsync(Action action) { action(); return ValueTask.CompletedTask; }
        public void RunPending() { while (pending.TryDequeue(out var action)) action(); }
    }
}
