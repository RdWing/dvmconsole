// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleOperationalRuntimeTests
{
    [Fact]
    public async Task IncompleteCompositionFailsExplicitlyAndCannotBeComposedTwice()
    {
        await using var services = new ConsoleSessionServices();
        var runtime = new ConsoleOperationalRuntime(services, []);
        Assert.Contains("Connections", Assert.Throws<InvalidOperationException>(() => runtime.Connections).Message);
        Assert.Contains("AudioSettings", Assert.Throws<InvalidOperationException>(() => runtime.AudioSettings).Message);
        Assert.Throws<InvalidOperationException>(runtime.CompleteComposition);
        Assert.False(runtime.IsComposed);
        runtime.BeginComposition();
        Assert.Throws<InvalidOperationException>(runtime.BeginComposition);
    }

    [Fact]
    public async Task WebPlaybackOutlivesItsAdapterButRetiresEvenWhenAdapterCleanupFails()
    {
        var services = new ConsoleSessionServices();
        var graph = new ConsoleOperationalRuntime(services, []);
        graph.RegisterWebPlaybackOwnership("web", () => ValueTask.FromException(new IOException("Adapter cleanup failed.")));
        var adapter = new ConsoleWebStreamSession([], preferences: null,
            createRuntime: observer => graph.InitializeWebPlayback(new(
                () => throw new InvalidOperationException("No endpoint should be opened."), () => null), observer));
        await adapter.DisposeAsync();
        // Detaching a host facade does not dispose the application-owned player.
        await graph.WebPlayback!.ResetAudioBackendAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => services.DisposeAsync().AsTask());
        Assert.IsType<IOException>(failure.InnerException);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => graph.WebPlayback.ResetAudioBackendAsync());
    }

    [Fact]
    public async Task SnapshotContextAttachesKeysAfterPreparationWithoutDuplicateObservers()
    {
        await using var services = new ConsoleSessionServices();
        var runtime = new ConsoleOperationalRuntime(services, []);
        var mute = new ReceiveMuteState();
        var context = new ConsoleSnapshotContextSource(null, (_, _) => null, () => false,
            () => null, () => new Dictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>>());
        var snapshots = runtime.GetOrCreateSnapshots(new(null, [], [], []), context, new());
        runtime.ObserveSnapshotContext(mute, runtime.RecordingPlaybackState, null);
        using var ring = new DvmConsole.Media.P25KeyRing("Test", new DvmConsole.Core.Configuration.KeyContainer());
        await using var keys = new P25KeyRetrievalCoordinator(ring, (_, _) => Task.CompletedTask);
        runtime.ObserveSnapshotContext(mute, runtime.RecordingPlaybackState, null, keys);
        runtime.ObserveSnapshotContext(mute, runtime.RecordingPlaybackState, null, keys);
        int changes = 0;
        snapshots.Changed += (_, _) => changes++;
        await keys.Schedule("Test", [(0x84, 1)], () => true, (_, _) => { }, (_, _) => true,
            (_, _) => { }, exception => throw exception);
        Assert.True(keys.TryApply("Test", 0x84, 1, new byte[32]));
        Assert.Equal(1, changes);
        await services.DisposeAsync();
        keys.Cancel("Test");
        Assert.Equal(1, changes);
    }

    [Fact]
    public void MeterResetDiscardsQueuedAudioBeforeNewTraffic()
    {
        var updates = new List<ChannelAudioMeterUpdate>();
        var graph = new ConsoleOperationalRuntime(new ConsoleSessionServices(), []);
        graph.InitializeMeters(updates.Add, updates.Add);
        var channel = new ChannelId(new DvmConsole.Operations.ChannelSessionId("North",
            DvmConsole.Core.Runtime.ChannelProtocol.P25, 100, 0, "Dispatch"));
        short[] samples = Enumerable.Repeat((short)8000, 160).ToArray();
        graph.Meters.Observe(channel, 1, samples, ChannelAudioDirection.Receive);
        graph.Meters.Reset();
        graph.Meters.Advance();
        Assert.False(graph.Meters.HasActivity);
        Assert.Empty(updates);
        Assert.True(graph.Meters.Observe(channel, 2, samples, ChannelAudioDirection.Receive));
        graph.Meters.Advance();
        Assert.Equal(2u, Assert.Single(updates).StreamId);
    }

    [Fact]
    public async Task FailedHostPreparationRetiresPartialGraphInGlobalReverseOrder()
    {
        var retired = new List<string>();
        var services = new ConsoleSessionServices(timing => retired.Add(timing.Name));
        var graph = new ConsoleOperationalRuntime(services, []);
        string[] expected = [];
        bool detached = false;
        bool radioDetached = false;
        var status = new ConsoleSessionStatus();
        int snapshotChanges = 0;
        var mute = new ReceiveMuteState();
        var playback = new RecordingPlaybackChannelState();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConsoleSessionConstruction.CreateAsync<object>(services, _ =>
            {
                graph.BindRadios([]);
                graph.InitializeRadioIngress(services.Connection, "radio-ingress", _ => radioDetached = true);
                graph.RegisterReceiveAudioOwnership();
                services.Audio.Register("host-endpoint", () => ValueTask.CompletedTask);
                graph.RegisterRecordingOwnership(services.Recording);
                graph.RegisterRecordingPlaybackOwnership("history-playback");
                graph.RegisterPatchOwnership(() => detached = true);
                graph.RegisterReceiveWorkOwnership();
                graph.RegisterReceiveOutputOwnership();
                var context = new ConsoleSnapshotContextSource(null, (_, _) => null,
                    () => false, () => null,
                    () => new Dictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>>());
                graph.GetOrCreateSnapshots(new(null, [], [], []), context, status).Changed +=
                    (_, _) => snapshotChanges++;
                graph.ObserveSnapshotContext(mute, playback, null);
                expected = services.SnapshotOwnership().Reverse().Select(item => item.Name).ToArray();
                throw new InvalidOperationException("Host endpoint preparation failed.");
            }).AsTask());

        status.SetConsole("After rollback");
        mute.SetGlobalMuted(true);
        playback.Apply(RecordingId.New(), true, null);
        Assert.Equal(0, snapshotChanges);
        Assert.True(detached);
        Assert.True(radioDetached);
        Assert.Equal(expected, retired);
        Assert.Equal(0, services.Count);
        await services.DisposeAsync();
        Assert.Equal(expected, retired);
    }
}
