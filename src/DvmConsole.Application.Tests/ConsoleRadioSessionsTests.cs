// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using Xunit;
using DvmConsole.Vocoder;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleRadioSessionsTests
{
    [Fact]
    public async Task RetiredRadioLifecycleRejectsLateConnectionEvenBeforeHostFlagChanges()
    {
        var state = ConsoleSessionState.Create(Configuration());
        await using var services = new ConsoleSessionServices();
        var runtime = ConsoleOperationalRuntime.Prepare(services, state);
        var radio = new Radio("First");
        var host = new LifecyclePort();
        var controller = new ConsoleRadioLifecycleController(runtime, state,
            new Dictionary<SystemId, IRadioSession> { [radio.SystemId] = radio }, null,
            new Dictionary<SystemId, P25KeyRequestPort>(), new ConsoleSubscriberCommandDispatcher(SystemClock.Instance), host);
        Assert.True(state.Terminal.TryClose());

        controller.ApplyConnection(new(radio.SystemId, radio.Name, RadioConnectionState.Disconnected,
            "Late completion", DateTimeOffset.UtcNow));

        Assert.Equal(0, host.ConnectionNotifications);
        // The retired callback must not seed a new connection generation either.
        Assert.True(runtime.ObserveConnectionState(radio.SystemId, radio.Name, RadioConnectionState.Disconnected).Changed);
    }

    private sealed class LifecyclePort : IConsoleRadioLifecyclePort
    {
        public bool IsStopping => false;
        public int ConnectionNotifications { get; private set; }
        public void ConnectionChanged(IRadioSession radio, RadioConnectionSnapshot snapshot, ConsoleConnectionChange transition)
            => ConnectionNotifications++;
        public void ChannelChanged(ChannelId channel) { }
        public void AuthorityChanged(TalkgroupAuthorityRecord record, IReadOnlyList<ChannelId> unavailable, int stoppedPatchTargets) { }
        public void KeyStateChanged(IRadioSession radio) { }
        public void KeyReceived(IRadioSession radio, RadioP25KeyResponse response, bool accepted, string message) { }
        public void PublishStatus(string message) { }
    }

    [Fact]
    public async Task VerboseDiagnosticsRestoreAndPersistBeforeChangingEveryRadio()
    {
        var first = new Radio("First");
        var second = new Radio("Second");
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(second))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), new ImmediateDelay(), null!, []);
        Preferences? preferences = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (prepared, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Preferences: preferences = new(prepared.Channels.Keys.First())));
        Assert.True(session.CanSaveDiagnosticSettings);
        Assert.True(first.Verbose && second.Verbose && session.VerboseLoggingEnabled);
        preferences!.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => session.SetVerboseLoggingAsync(false).AsTask());
        Assert.True(first.Verbose && second.Verbose && session.VerboseLoggingEnabled);
        preferences.FailSave = false;
        await session.SetVerboseLoggingAsync(false);
        Assert.False(first.Verbose || second.Verbose || session.VerboseLoggingEnabled || preferences.Verbose);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetVerboseLoggingAsync(true).AsTask());
    }

    [Fact]
    public async Task LiveSessionRejectsAuthorityFromTheWrongRadioAndDetachesOnRetirement()
    {
        var first = new Radio("First");
        var second = new Radio("Second");
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(second))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), new ImmediateDelay(), null!, []);
        ConsoleSessionState? state = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (prepared, _) =>
        {
            state = prepared;
            return new(host, plan.Systems, new PassthroughNormalizer());
        });
        ConsoleChannelState channel = state!.Channels.Values.First();
        var authority = new TalkgroupAuthorityRecord(first.SystemId,
            [new(channel.Id, TargetAuthorityState.Unavailable, "Unavailable")], DateTimeOffset.UtcNow);
        TargetAuthorityState initial = channel.Authority;

        second.PublishAuthority(authority);
        Assert.Equal(initial, channel.Authority);
        first.PublishAuthority(authority);
        Assert.Equal(TargetAuthorityState.Unavailable, channel.Authority);

        await session.DisposeAsync();
        first.PublishAuthority(authority with { Channels = [new(channel.Id, TargetAuthorityState.Available, null)] });
        Assert.Equal(TargetAuthorityState.Unavailable, channel.Authority);
    }

    [Fact]
    public async Task LiveSessionRequestsAndAppliesRemoteKeysWithoutPresentationOrImportedKeys()
    {
        var configuration = Configuration();
        configuration.Zones[0].Channels[0].Algo = "aes";
        configuration.Zones[0].Channels[0].KeyId = "1";
        byte[] material = Enumerable.Repeat((byte)0x33, 32).ToArray();
        using var keys = new DvmConsole.Media.P25KeyRing();
        var radio = new Radio("First") { TransmitAvailable = true };
        int requests = 0;
        radio.RequestKey = (algorithm, key) =>
        {
            requests++;
            radio.PublishKey(new(radio.SystemId, algorithm, key, material));
        };
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(radio)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), new ImmediateDelay(), null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), P25: keys));
        radio.PublishConnection();
        Assert.Equal(1, requests);
        Assert.True(keys.TryResolve("First", 0x84, 1, out var resolved));
        Assert.Equal(material, resolved.ToArray());
        Assert.Contains("received through FNE/KMM", session.CaptureSnapshot().StatusText);
        radio.PublishKey(new(SystemId.FromName("Second"), 0x84, 1, material));
        Assert.False(keys.TryResolve("Second", 0x84, 1, out _));
        radio.TransmitAvailable = false;
        radio.PublishConnection();
        Assert.False(keys.TryResolve("First", 0x84, 1, out _));
        radio.PublishKey(new(radio.SystemId, 0x84, 1, material));
        Assert.False(keys.TryResolve("First", 0x84, 1, out _));
        radio.TransmitAvailable = true;
        radio.PublishConnection();
        Assert.Equal(2, requests);
        Assert.True(keys.TryResolve("First", 0x84, 1, out _));
        await session.DisposeAsync();
        radio.TransmitAvailable = true;
        radio.PublishConnection();
        radio.PublishKey(new(radio.SystemId, 0x84, 1, material));
        Assert.False(keys.TryResolve("First", 0x84, 1, out _));
        Assert.Equal(2, requests);
    }

    private sealed class ImmediateDelay : IApplicationDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task SharedRadioEndpointPreservesStartResetAndClearsKeyRequestsOnlyAfterStop()
    {
        var radio = new Radio("Transport") { TransmitAvailable = true };
        var keys = new P25KeyRequestState();
        int resets = 0;
        int requests = 0;
        keys.Request(0x84, 1, () => requests++);
        keys.ObserveResponse(0x84, 1);
        var endpoint = RadioConnectionEndpoint.FromSession(SystemId.FromName("Display"), "Display", radio, keys, () => resets++);
        Assert.Equal(SystemId.FromName("Display"), endpoint.Id);
        Assert.Equal("Display", endpoint.Name);
        Assert.True(endpoint.IsActive());
        using var cancellation = new CancellationTokenSource();
        radio.Start = token =>
        {
            Assert.Equal(cancellation.Token, token);
            Assert.Equal(1, resets);
            return Task.FromException(new IOException("Start failed"));
        };
        await Assert.ThrowsAsync<IOException>(() => endpoint.StartAsync(cancellation.Token).AsTask());
        Assert.True(keys.HasResponse(0x84, 1));
        radio.Stop = _ => Task.FromException(new IOException("Stop failed"));
        await Assert.ThrowsAsync<IOException>(() => endpoint.StopAsync(default).AsTask());
        Assert.True(keys.HasResponse(0x84, 1));
        cancellation.Cancel();
        radio.Stop = token => Task.FromCanceled(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => endpoint.StopAsync(cancellation.Token).AsTask());
        Assert.True(keys.HasResponse(0x84, 1));
        radio.Stop = null;
        await endpoint.StopAsync(default);
        Assert.False(keys.HasResponse(0x84, 1));
        keys.Request(0x84, 1, () => requests++);
        Assert.Equal(2, requests);
        Assert.Equal(0, radio.DisposeCount);
        Assert.NotNull(endpoint.Abort);
        endpoint.Abort();
        Assert.Equal(1, radio.AbortCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingCleanupFailureDoesNotFailListeningButCancellationStillPropagates(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var capture = new CheckpointCapture
        {
            Prune = token =>
            {
                if (!cancel) return Task.FromException<int>(new IOException("Catalog unavailable"));
                cancellation.Cancel();
                return Task.FromCanceled<int>(token);
            }
        };
        var preferences = new RecordingPreferences { Policy = new(7, true) };
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Recordings: capture, Preferences: preferences));
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ActivateListeningAsync(cancellation.Token));
        else
        {
            await session.ActivateListeningAsync();
            Assert.Equal("Listening restored; recording cleanup failed: Catalog unavailable", session.CaptureSnapshot().StatusText);
        }
        Assert.True(session.Execution.Snapshot.CanReceive);
        Assert.True(session.IsAcceptingCommands);
        await session.ConnectAsync();
        if (!cancel)
        {
            await Assert.ThrowsAsync<IOException>(() => session.SetRecordingRetentionAsync(new(30, true)).AsTask());
            Assert.Equal(new RecordingRetentionPolicy(30, true), session.RecordingRetention);
            Assert.Contains("saved, but cleanup failed", session.CaptureSnapshot().StatusText);
        }
    }

    [Fact]
    public async Task ReceiveBufferingPublishesOnlyPersistedConnectionOptions()
    {
        var preferences = new BufferingPreferences();
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Preferences: preferences));
        var initial = session.ReceiveBuffering;
        preferences.InitialMilliseconds = 300;
        await session.ActivateListeningAsync();
        var before = session.ReceiveBuffering;
        SystemId first = SystemId.FromName("First");
        SystemId second = SystemId.FromName("Second");
        Assert.Equal(240, initial[first].DmrMilliseconds);
        Assert.Equal(300, before[first].DmrMilliseconds);
        var changed = before[first] with { DmrMilliseconds = 180, DmrAdaptive = false };
        preferences.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => session.SetReceiveBufferingAsync(first, changed).AsTask());
        Assert.Same(before, session.ReceiveBuffering);
        preferences.Fail = false;
        await session.SetReceiveBufferingAsync(first, changed);
        Assert.Equal(changed, session.ReceiveBuffering[first]);
        Assert.Equal(300, before[first].DmrMilliseconds);
        Assert.Equal(before[second], session.ReceiveBuffering[second]);
        await Assert.ThrowsAsync<ArgumentException>(() => session.SetReceiveBufferingAsync(SystemId.FromName("Absent"), changed).AsTask());
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetReceiveBufferingAsync(first, new()).AsTask());
    }

    private sealed class BufferingPreferences : IConsoleReceivePreferences, IConsoleReceiveBufferingStore
    {
        public int InitialMilliseconds { get; set; } = 240;
        public bool Fail { get; set; }
        public ValueTask<global::System.Collections.Immutable.ImmutableDictionary<string, ConsoleReceiveBufferingOptions>> LoadReceiveBufferingAsync(
            IReadOnlyList<string> systems, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(global::System.Collections.Immutable.ImmutableDictionary.CreateRange(
                systems.Select(name => new KeyValuePair<string, ConsoleReceiveBufferingOptions>(name, new(DmrMilliseconds: InitialMilliseconds)))));
        public ValueTask SaveReceiveBufferingAsync(string system, ConsoleReceiveBufferingOptions options, CancellationToken cancellationToken = default)
            => Fail ? ValueTask.FromException(new IOException("Storage unavailable")) : ValueTask.CompletedTask;
        public ValueTask<global::System.Collections.Immutable.ImmutableDictionary<ChannelId, ChannelReceivePreferences>> LoadAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(global::System.Collections.Immutable.ImmutableDictionary<ChannelId, ChannelReceivePreferences>.Empty);
        public ValueTask SaveAsync(ChannelId channel, ChannelReceivePreferenceChange change, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RecordingRetentionRequiresAcceptedPersistedPolicyBeforeCleanup()
    {
        var capture = new CheckpointCapture();
        var preferences = new RecordingPreferences();
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Recordings: capture, Preferences: preferences));
        Assert.Equal(30, capture.RetentionDays);
        preferences.Policy = new(45);
        await session.ActivateListeningAsync();
        Assert.Equal(45, capture.RetentionDays);
        Assert.Equal(0, capture.Prunes);
        await session.PreviewRecordingRetentionAsync(1);
        Assert.Equal(0, capture.Prunes);
        preferences.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => session.SetRecordingRetentionAsync(new(1, true)).AsTask());
        Assert.Equal(45, capture.RetentionDays);
        Assert.False(session.RecordingRetention.Accepted);
        Assert.Equal(0, capture.Prunes);
        preferences.Fail = false;
        await session.SetRecordingRetentionAsync(new(1, true));
        Assert.Equal(1, capture.RetentionDays);
        Assert.Equal(1, capture.Prunes);
        await session.SetRecordingRetentionAsync(new(0, true));
        await session.ActivateListeningAsync();
        Assert.Equal(1, capture.Prunes);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetRecordingRetentionAsync(new(7, true)).AsTask());
        Assert.Equal(0, capture.RetentionDays);
    }

    private sealed class RecordingPreferences : IConsoleReceivePreferences, IConsoleRecordingSettingsStore
    {
        public bool Fail { get; set; }
        public RecordingRetentionPolicy Policy { get; set; } = new(30);
        public ValueTask<RecordingRetentionPolicy> LoadRecordingRetentionAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Policy);
        public ValueTask SaveRecordingRetentionAsync(RecordingRetentionPolicy policy, CancellationToken cancellationToken = default)
        {
            if (Fail) return ValueTask.FromException(new IOException("Storage unavailable"));
            Policy = policy;
            return ValueTask.CompletedTask;
        }
        public ValueTask<global::System.Collections.Immutable.ImmutableDictionary<ChannelId, ChannelReceivePreferences>> LoadAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(global::System.Collections.Immutable.ImmutableDictionary<ChannelId, ChannelReceivePreferences>.Empty);
        public ValueTask SaveAsync(ChannelId channel, ChannelReceivePreferenceChange change, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SharedStatusInvalidatesComposedSnapshotAndUnsubscribesOnRetirement()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        ConsoleSessionState? state = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (prepared, _) =>
        {
            state = prepared;
            return new(host, plan.Systems, new PassthroughNormalizer());
        });
        await session.ConnectAsync();
        Assert.Equal("FNE connection services started; waiting for login acknowledgements.", session.CaptureSnapshot().StatusText);
        var original = session.CaptureSnapshot();
        state!.Status.SetConsole("Shared runtime message");
        var updated = session.CaptureSnapshot();
        Assert.Equal("Shared runtime message", updated.StatusText);
        Assert.NotEqual(updated.StatusText, original.StatusText);
        foreach (var (id, channel) in original.Channels) Assert.Same(channel, updated.Channels[id]);
        await session.DisposeAsync();
        int publications = 0;
        session.ControlStateInvalidated += (_, _) => publications++;
        state.Status.SetConsole("Retired session message");
        Assert.Equal(0, publications);
    }

    [Fact]
    public void SharedSubscriberAuditRetainsFailuresAndImmutableHistoryWithoutRetryingTransport()
    {
        var clock = new SubscriberClock(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
        var dispatcher = new ConsoleSubscriberCommandDispatcher(clock, historyLimit: 2);
        var radio = new Radio("Dispatch") { TransmitAvailable = true };
        var accepted = dispatcher.Submit(radio.SystemId, radio, radio, ConsoleSubscriberCommand.Page, 10);
        var previous = dispatcher.History;
        radio.SubscriberSendFailure = new IOException("UDP send failed");
        var failed = dispatcher.Submit(radio.SystemId, radio, radio, ConsoleSubscriberCommand.RadioCheck, 20);
        var invalid = dispatcher.Submit(radio.SystemId, radio, radio, ConsoleSubscriberCommand.Inhibit, 0);
        Assert.Equal(clock.UtcNow, accepted.Timestamp);
        Assert.Equal("Dispatch: Page to RID 10 sent.", accepted.StatusText);
        Assert.Equal("Unable to send command: UDP send failed", failed.Detail);
        Assert.False(failed.Submitted);
        Assert.False(invalid.Submitted);
        Assert.Equal([invalid, failed], dispatcher.History);
        Assert.Equal(accepted, Assert.Single(previous));
        Assert.Single(radio.SubscriberCommands);
    }

    private sealed record SubscriberClock(DateTimeOffset UtcNow) : IClock;

    [Fact]
    public async Task MediaFailureRemainsVisibleDespiteFaultingLogObserverAndStopsAfterRetirement()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        var logs = new List<ConsoleLogEvent>();
        session.LogPublished += (_, _) => throw new InvalidOperationException("detached log observer");
        session.LogPublished += (_, entry) => logs.Add(entry);
        var channel = session.CaptureTopology().Channels.First().Id;
        var observation = (IReceiveFrameObservationPort)session;
        observation.ShowFault(channel, new IOException("Playback endpoint unavailable."));
        Assert.Equal(ConsoleLogLevel.Error, Assert.Single(logs).Level);
        Assert.Equal("Playback endpoint unavailable.", session.CaptureSnapshot().StatusText);
        ((IReceiveOutputLifetimePort)session).ObserveRecovery(TimeSpan.FromMilliseconds(25), "Output restored.");
        var health = await session.CaptureHealthAsync();
        Assert.Equal(1, health.RouteRecoveryAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(25), health.LastRouteRecoveryDuration);
        Assert.Equal("Output restored.", health.LastRouteRecoveryResult);
        Assert.Null(health.RecordingFinalization);
        Assert.Null(health.RecordingCatalog);
        Assert.Equal(DvmConsole.Operations.MicrophoneHealthState.Stopped, health.Microphone.State);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CaptureHealthAsync());
        observation.ShowFault(channel, new IOException("Late retired callback."));
        Assert.Single(logs);
    }

    [Theory]
    [InlineData(ConsoleSubscriberCommand.Page)]
    [InlineData(ConsoleSubscriberCommand.RadioCheck)]
    [InlineData(ConsoleSubscriberCommand.Inhibit)]
    [InlineData(ConsoleSubscriberCommand.Uninhibit)]
    public async Task SubscriberCommandsUseRadioCapabilityAndRejectUnavailableLifecycle(ConsoleSubscriberCommand command)
    {
        var first = new Radio("First") { TransmitAvailable = true };
        var second = new Radio("Second");
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(second))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        var sent = await session.SendSubscriberCommandAsync(first.SystemId, command, 1234);
        Assert.True(sent.Submitted);
        Assert.Equal((command, 1234u), Assert.Single(first.SubscriberCommands));
        Assert.Contains("awaiting subscriber acknowledgement", sent.Detail);
        Assert.False((await session.SendSubscriberCommandAsync(second.SystemId, command, 1234)).Submitted);
        Assert.Empty(second.SubscriberCommands);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SendSubscriberCommandAsync(first.SystemId, command, 0));
        session.Execution.SetForeground(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendSubscriberCommandAsync(first.SystemId, command, 1234));
        Assert.Single(first.SubscriberCommands);
        Assert.Equal(2, session.SubscriberCommandHistory.Count);
    }
    [Fact]
    public async Task LaterFactoryFailureRetiresEarlierSession()
    {
        var first = new Radio("First");
        await Assert.ThrowsAsync<IOException>(() => ConsoleRadioSessions.CreateAsync(State(),
            [Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
             Binding("Second", _ => throw new IOException("construction failed"))]).AsTask());
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task CancellationAfterLateCompletionRetiresResultBeforeReturning()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new Radio("First");
        var late = new TaskCompletionSource<IRadioSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        int nextCalls = 0;
        Task<ConsoleRadioSessions> creation = ConsoleRadioSessions.CreateAsync(State(),
            [Binding("First", _ => new ValueTask<IRadioSession>(late.Task)),
             Binding("Second", _ => { nextCalls++; return ValueTask.FromResult<IRadioSession>(new Radio("Second")); })],
            cancellation.Token).AsTask();
        cancellation.Cancel();
        late.SetResult(first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, nextCalls);
    }

    [Fact]
    public async Task MismatchedFactoryResultIsStillOwnedAndRetired()
    {
        var wrong = new Radio("Wrong");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConsoleRadioSessions.CreateAsync(State(),
            [Binding("First", _ => ValueTask.FromResult<IRadioSession>(wrong)),
             Binding("Second", _ => throw new InvalidOperationException("must not run"))]).AsTask());
        Assert.Equal(1, wrong.DisposeCount);
    }

    [Theory]
    [InlineData("First")]
    [InlineData("Unknown")]
    public async Task InvalidBindingsRejectBeforeAnyFactoryRuns(string secondName)
    {
        int calls = 0;
        ValueTask<IRadioSession> Create(CancellationToken _) { calls++; return ValueTask.FromResult<IRadioSession>(new Radio("First")); }
        await Assert.ThrowsAsync<ArgumentException>(() => ConsoleRadioSessions.CreateAsync(State(),
            [Binding("First", Create), Binding(secondName, Create)]).AsTask());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SuccessfulOwnershipRetiresEachSessionOnceAcrossConcurrentDisposal()
    {
        var first = new Radio("First");
        var second = new Radio("Second");
        ConsoleRadioSessions owner = await ConsoleRadioSessions.CreateAsync(State(),
            [Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
             Binding("Second", _ => ValueTask.FromResult<IRadioSession>(second))]);
        Assert.Same(first, owner.Sessions[first.SystemId]);
        Assert.Equal(0, first.DisposeCount);
        await Task.WhenAll(owner.DisposeAsync().AsTask(), owner.DisposeAsync().AsTask());
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task PreparedPlanDefersFactoriesAndRejectsUnknownSystems()
    {
        int calls = 0;
        var plan = new ConsoleRadioSessionPlan([Binding("First", _ =>
        {
            calls++;
            return ValueTask.FromResult<IRadioSession>(new Radio("First"));
        })]);
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<ArgumentException>(() => plan.CreateAsync(
            new RadioSystemDescriptor(SystemId.FromName("Other"), "Other", "FNE", new Dictionary<string, string>())).AsTask());
        Assert.Equal(0, calls);
        await using IRadioSession radio = await plan.CreateAsync(plan.Systems[0]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PreparedPlanStillValidatesAllSystemsBeforeOpeningAnyEndpoint()
    {
        int calls = 0;
        var plan = new ConsoleRadioSessionPlan([Binding("First", _ =>
        {
            calls++;
            return ValueTask.FromResult<IRadioSession>(new Radio("First"));
        })]);
        await Assert.ThrowsAsync<ArgumentException>(() => ConsoleRadioSessions.CreateAsync(State(), plan).AsTask());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ReceiveSessionRejectsInvalidConfigurationBeforePreparingHostServices()
    {
        var configuration = Configuration();
        configuration.Zones[0].Channels[0].Tgid = "invalid";
        int prepared = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ConsoleReceiveSession.CreateAsync(configuration, (_, _) =>
        { prepared++; throw new InvalidOperationException("must not prepare"); }).AsTask());
        Assert.Equal(0, prepared);
    }

    [Fact]
    public async Task RuntimeAttachmentFailureRetiresIngressAndRadios()
    {
        var first = new Radio("First");
        var second = new Radio("Second");
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(second))]);
        var failure = new IOException("Cannot start session supervision.");
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new FailingScheduler(failure), SystemApplicationDelay.Instance, null!, []);
        ConsoleSessionState? state = null;
        IOException reported = await Assert.ThrowsAsync<IOException>(() => ConsoleReceiveSession.CreateAsync(
            Configuration(), (prepared, _) =>
            {
                state = prepared;
                return new(host, plan.Systems, new PassthroughNormalizer());
            }).AsTask());
        Assert.Same(failure, reported);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.True(state!.Terminal.IsClosed);
        ConsoleChannelState channel = Assert.Single(state.Channels.Values);
        TargetAuthorityState initial = channel.Authority;
        first.PublishAuthority(new(first.SystemId,
            [new(channel.Id, TargetAuthorityState.Unavailable, "Retired")], DateTimeOffset.UtcNow));
        Assert.Equal(initial, channel.Authority);
    }

    private sealed class FailingScheduler(Exception failure) : IApplicationScheduler
    {
        public IScheduledWork CreatePeriodic(TimeSpan interval, Func<CancellationToken, ValueTask> callback,
            bool startImmediately = true) => throw failure;
    }

    [Fact]
    public async Task ReceiveSessionOwnsPreparedRadiosAndClosesCommandAdmissionOnDisposal()
    {
        var first = new Radio("First");
        var second = new Radio("Second");
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(second))]);
        var scheduler = new DormantScheduler();
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, scheduler, SystemApplicationDelay.Instance, null!, []);
        ConsoleReceiveSession session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        Assert.All(session.CaptureTopology().Channels, channel => Assert.True(channel.ReceiveOnly));
        await session.ConnectAsync();
        var unmuted = session.CaptureSnapshot();
        await session.SetOutputMutedAsync(true);
        Assert.True(session.OutputMuted);
        Assert.All(session.CaptureSnapshot().Channels.Values,
            channel => Assert.Equal("global output mute", channel.EffectiveMuteReason));
        Assert.All(unmuted.Channels.Values, channel => Assert.Null(channel.EffectiveMuteReason));
        Assert.True(session.Execution.Snapshot.CanReceive);
        session.SetAudioAvailable(false, "Interrupted");
        await session.SetOutputMutedAsync(false);
        Assert.False(session.OutputMuted);
        Assert.All(session.CaptureSnapshot().Channels.Values, channel => Assert.Null(channel.EffectiveMuteReason));
        Assert.False(session.Execution.Snapshot.CanReceive);
        await session.ResumeAudioAsync(_ => Task.CompletedTask);
        Assert.True(session.Execution.Snapshot.CanReceive);
        Assert.False(session.OutputMuted);
        Assert.Equal(1, first.Starts);
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(3, scheduler.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ConnectAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetOutputMutedAsync(true).AsTask());
    }

    [Fact]
    public async Task SessionRetirementCancelsPendingCheckpointWithoutStoppingEarly()
    {
        var capture = new CheckpointCapture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.Checkpoint = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Recordings: capture));
        Task checkpoint = session.CheckpointAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(session.IsAcceptingCommands);
        try
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkpoint);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CheckpointFailureReportsStorageProblemWithoutPausingListening()
    {
        var capture = new CheckpointCapture { Checkpoint = _ => Task.FromException(new IOException("Checkpoint storage failure")) };
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Recordings: capture));
        await Assert.ThrowsAsync<IOException>(() => session.CheckpointAsync().AsTask());
        Assert.Contains("Checkpoint storage failure", session.CaptureSnapshot().StatusText);
        Assert.True(session.Execution.Snapshot.CanReceive);
        capture.Checkpoint = _ => Task.CompletedTask;
        await session.CheckpointAsync();
    }

    private sealed class CheckpointCapture : IReceiveRecordingSession, IRecordingRetentionControl
    {
        public int RetentionDays { get; set; }
        public int Prunes { get; private set; }
        public Func<CancellationToken, Task<int>> Prune { get; set; } = _ => Task.FromResult(0);
        public Task<RecordingRetentionPreview> PreviewRetentionAsync(int days, CancellationToken cancellationToken = default)
            => Task.FromResult(new RecordingRetentionPreview(days == 0 ? null : DateTimeOffset.UtcNow.AddDays(-days), 0));
        public Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
        { Prunes++; return Prune(cancellationToken); }
        public Func<CancellationToken, Task> Checkpoint { get; set; } = _ => Task.CompletedTask;
        public bool CanWrite => true;
        public event Action<ChannelId>? StateChanged { add { } remove { } }
        public ReceiveRecordingState CaptureState(ChannelId channel) => default;
        public Task DrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CheckpointAsync(CancellationToken cancellationToken = default) => Checkpoint(cancellationToken);
        public void WriteEpisodeSamples(ChannelRecordingDescriptor channel, uint episodeStreamId, uint physicalStreamId,
            uint sourceId, ReadOnlyMemory<short> samples, long? receiveEpisodeId = null)
        { }
        public void ObserveEpisodeTraffic(ChannelRecordingDescriptor channel, uint episodeStreamId, uint physicalStreamId,
            IRadioMediaFrame traffic, long? receiveEpisodeId = null)
        { }
        public void StopEpisode(ChannelRecordingDescriptor channel, long receiveEpisodeId) { }
        public void StopChannel(ChannelRecordingDescriptor channel) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AudioInterruptionCancelsPendingRecordingStartupEvenAfterRecovery()
    {
        var store = new PendingRecordingStore();
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, store, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        var id = new RecordingId(Guid.NewGuid());
        var started = session.PlayRecordingAsync(id).AsTask();
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.SetAudioAvailable(false, "Interrupted");
        session.SetAudioAvailable(true, "Recovered");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => started);
        Assert.False(session.IsRecordingPlaying(id));
    }

    private sealed class PendingRecordingStore : IRecordingStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Stream> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IRecordingWriteHandle> CreateAsync(CallId callId, ChannelId channelId, DateTimeOffset startedAt,
            string mediaType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async ValueTask<Stream> OpenReadAsync(RecordingId id, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return await pending.Task.WaitAsync(cancellationToken);
        }
        public async IAsyncEnumerable<RecordingDescriptor> ListAsync(
            [global::System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }

    [Fact]
    public async Task ReplacementQuiescenceJoinsDelayedAudioIntentBeforeRollback()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        ChannelId id = default;
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (state, _) =>
        {
            id = state.Channels.Keys.First();
            return new(host, plan.Systems, new PassthroughNormalizer());
        });
        await session.SetChannelGainAsync(id, 2);
        Task pending = session.SetChannelGainAsync(id, 3).AsTask();
        await session.QuiesceAsync(CancellationToken.None);
        Assert.True(pending.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        session.ReactivateAfterFailedReplacement();
        Assert.Equal(2, session.CaptureSnapshot().Channels[id].Gain);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedSessionCannotRecoverOutputUntilListeningIsActivated(bool recoverAfterActivation)
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var scheduler = new DormantScheduler();
        var audio = new CountingOutputBackend();
        var host = new ConsoleHostServices(plan, audio, new PatchVocoderFactory(), null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, scheduler, SystemApplicationDelay.Instance, null!, []);
        ConsoleChannelState? channel = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (state, _) =>
        {
            channel = state.Channels.Values.First();
            return new(host, plan.Systems, new PassthroughNormalizer(), Preferences: new Preferences(channel.Id));
        });

        // Run the recovery timer while a replacement is still only prepared.
        // Saved RX intent must not compete with the outgoing physical output.
        await scheduler.TickAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, audio.OutputOpens);
        if (recoverAfterActivation) channel!.Operator.SetAudioEnabled(false);
        await session.ActivateListeningAsync();
        Assert.Equal(recoverAfterActivation ? 0 : 1, audio.OutputOpens);
        if (recoverAfterActivation) channel!.Operator.SetAudioEnabled(true);
        await scheduler.TickAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, audio.OutputOpens);
    }

    private sealed class CountingOutputBackend : IAudioBackendFactory, IAudioBackend
    {
        public int OutputOpens { get; private set; }
        public string Name => "Output ownership test";
        public IAudioBackend Create(AudioBackendConfiguration configuration) => this;
        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output ? [new("default", "Output", direction, true)] : [];
        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format) => throw new NotSupportedException();
        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        {
            OutputOpens++;
            return new Playback(format);
        }
        public void Dispose() { }
        private sealed class Playback(PcmAudioFormat format) : IAudioPlayback
        {
            public PcmAudioFormat Format => format;
            public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
                => ValueTask.CompletedTask;
            public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ReceivePreferencesRestoreAndFailedSaveDoesNotPublishUnsavedGain()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        Preferences? preferences = null;
        ConsoleChannelState? restoredChannel = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (state, _) =>
        {
            preferences = new Preferences(state.Channels.Keys.First());
            restoredChannel = state.Channels[preferences.Id];
            return new(host, plan.Systems, new PassthroughNormalizer(), Preferences: preferences);
        });
        ChannelId id = preferences!.Id;
        Assert.False(restoredChannel!.RecordingSubscribers.Allows(42));
        Assert.True(restoredChannel.RecordingSubscribers.Allows(43));
        Assert.Equal(0.75, session.CaptureSnapshot().Channels[id].Gain);
        Assert.Equal(-0.5, session.CaptureSnapshot().Channels[id].Balance);
        Assert.True(session.CaptureSnapshot().Channels[id].ReceiveEnabled);
        // No backend was supplied: preparation must not open the physical output.
        await session.SetChannelGainAsync(id, 2);
        Assert.Equal(2, preferences.LastChange!.Gain);
        Assert.Equal(2, session.CaptureSnapshot().Channels[id].Gain);
        await session.SetRestoreSelectedChannelsAsync(false);
        Assert.False(session.RestoreSelectedChannelsOnStartup);
        Assert.True(session.CaptureSnapshot().Channels[id].ReceiveEnabled);
        preferences.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => session.SetRestoreSelectedChannelsAsync(true).AsTask());
        Assert.False(session.RestoreSelectedChannelsOnStartup);
        await Assert.ThrowsAsync<IOException>(() => session.SetChannelGainAsync(id, 3).AsTask());
        Assert.Equal(2, session.CaptureSnapshot().Channels[id].Gain);
        await session.FlushSettingsAsync(default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EncryptionSelectionRequiresSelectableConfigurationAndAvailableKey(bool selectable)
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        var configuration = Configuration();
        configuration.Zones[0].Channels[0].Algo = "aes";
        configuration.Zones[0].Channels[0].KeyId = "1";
        configuration.Zones[0].Channels[0].SelectableEncryption = selectable;
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), ManualInput: new AudioInputProcessingOptions()));
        ChannelId id = session.CaptureTopology().Channels[0].Id;
        Assert.True(session.CaptureSnapshot().Channels[id].SelectedTransmitEncrypted);
        await session.SetTransmitEncryptedAsync(id, false);
        Assert.Equal(!selectable, session.CaptureSnapshot().Channels[id].SelectedTransmitEncrypted);
        // Without a key, a selectable secure channel may go clear but cannot arm secure TX.
        await session.SetTransmitEncryptedAsync(id, true);
        Assert.Equal(!selectable, session.CaptureSnapshot().Channels[id].SelectedTransmitEncrypted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransmitSelectionsRespectHostCapabilityAndFailedPersistence(bool allowTransmit)
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        Preferences? preferences = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (prepared, _) =>
        {
            preferences = new Preferences(prepared.Channels.Keys.First());
            return new(host, plan.Systems, new PassthroughNormalizer(), Preferences: preferences,
                ManualInput: allowTransmit ? new AudioInputProcessingOptions() : null);
        });
        ChannelId id = preferences!.Id;
        Assert.Equal(allowTransmit, session.CaptureSnapshot().Channels[id].TransmitSelected);
        await session.SetPageSelectedAsync(id, true);
        await session.SetAlertSelectedAsync(id, true);
        Assert.Equal(allowTransmit, session.CaptureSnapshot().Channels[id].PageSelected);
        Assert.Equal(allowTransmit, session.CaptureSnapshot().Channels[id].AlertSelected);
        Assert.False(session.CaptureSnapshot().Channels[id].Transmitting);
        if (allowTransmit)
        {
            preferences.FailSave = true;
            await Assert.ThrowsAsync<IOException>(() => session.SetTransmitSelectedAsync(id, false).AsTask());
            Assert.True(session.CaptureSnapshot().Channels[id].TransmitSelected);
            preferences.FailSave = false;
        }
        await session.SetTransmitSelectedAsync(id, false);
        Assert.False(session.CaptureSnapshot().Channels[id].TransmitSelected);
    }

    [Fact]
    public async Task SavedGroupSelectionPreservesOtherTargetsAndNeverStartsTransmission()
    {
        var configuration = Configuration();
        configuration.Groups = [new() { Name = "Dispatch", Type = "multiselect" }];
        configuration.Zones[0].Channels.Add(new ChannelConfiguration
        { Name = "Tactical", System = "Second", Mode = "p25", Tgid = "101" });
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        Preferences? preferences = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (prepared, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Preferences: preferences = new(prepared.Channels.Keys.First()),
                ManualInput: new AudioInputProcessingOptions()));
        ChannelId member = session.CaptureTopology().Channels.First(channel => channel.Id != preferences!.Id).Id;
        await session.SaveGroupAsync("Dispatch", [member], false, false);
        var before = session.CaptureSnapshot();
        preferences!.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => session.AddGroupToTransmitSelectionAsync("Dispatch"));
        Assert.False(session.CaptureSnapshot().Channels[member].TransmitSelected);
        preferences.FailSave = false;
        Assert.Equal(1, await session.AddGroupToTransmitSelectionAsync("Dispatch"));
        Assert.True(session.CaptureSnapshot().Channels[member].TransmitSelected);
        Assert.True(session.CaptureSnapshot().Channels[preferences.Id].TransmitSelected);
        Assert.False(before.Channels[member].TransmitSelected);
        Assert.All(session.CaptureSnapshot().Channels.Values, channel => Assert.False(channel.Transmitting));
        Assert.False(session.IsSelectedPttRequested);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AddGroupToTransmitSelectionAsync("Dispatch"));
    }

    [Fact]
    public async Task GroupSaveRejectsReceiveOnlyDestinationsBeforePersistingButAllowsOneWaySources()
    {
        var configuration = Configuration();
        configuration.Groups = [new() { Name = "Patch" }];
        configuration.Zones[0].Channels[0].RxOnly = true;
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        Preferences? preferences = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (prepared, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Preferences: preferences = new(prepared.Channels.Keys.First())));
        var original = Assert.Single(session.SavedGroups);
        preferences!.FailSave = true;
        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            session.SaveGroupAsync("Patch", [preferences.Id], false, false));
        Assert.Contains("These members cannot transmit: Operations.", failure.Message);
        Assert.Same(original, Assert.Single(session.SavedGroups));
        preferences.FailSave = false;
        await session.SaveGroupAsync("Patch", [preferences.Id], false, true);
        Assert.True(Assert.Single(session.SavedGroups).OneWay);
    }

    [Fact]
    public async Task QuiescenceNotifiesListeningControlsAfterClosingAdmission()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        bool notified = false;
        session.ControlStateInvalidated += (_, _) => notified |= !session.IsAcceptingCommands;
        await session.QuiesceAsync(default);
        Assert.True(notified);
    }

    [Theory]
    [InlineData("dmr")]
    [InlineData("p25")]
    [InlineData("nxdn")]
    public async Task PortablePatchSourceDisablesEveryReceiveEnhancement(string protocol)
    {
        var configuration = Configuration();
        configuration.Groups = [new() { Name = "Patch" }];
        configuration.Zones[0].Channels[0].Mode = protocol;
        configuration.Zones[0].Channels.Add(new() { Name = "Target", System = "First", Mode = protocol, Tgid = "200" });
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var vocoder = new PatchVocoderFactory();
        var host = new ConsoleHostServices(plan, null!, vocoder, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (prepared, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Preferences: new Preferences(prepared.Channels.Keys.First()),
                ManualInput: new AudioInputProcessingOptions()));
        await session.SaveGroupAsync("Patch", session.CaptureTopology().Channels.Select(channel => channel.Id).ToArray(), true, true);
        Assert.NotNull(vocoder.Options);
        Assert.Equal(Enum.GetValues<VocoderMode>().Length, vocoder.Options.Count);
        Assert.All(vocoder.Options.Values, options =>
        {
            Assert.False(options.HighPassFilterEnabled);
            Assert.False(options.PeakingFilterEnabled);
            Assert.False(options.CompressorEnabled);
        });
    }

    private sealed class PatchVocoderFactory : IVocoderFactory, IVocoderBackend, IVocoderSession
    {
        public IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions>? Options;
        public IVocoderBackend Create(IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions>? receiveAudioProcessingOptions = null)
        { Options = receiveAudioProcessingOptions; return this; }
        public string Name => "Patch test";
        public bool IsAvailable => true;
        public IVocoderSession CreateSession(VocoderMode mode) => this;
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => throw new NotSupportedException();
        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [Fact]
    public async Task GroupEditsPublishAfterSavingAndRetainFrozenConfigurationIdentity()
    {
        var configuration = Configuration();
        configuration.Groups = [new() { Name = "Patch" }];
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        Preferences? preferences = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (prepared, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Preferences: preferences = new(prepared.Channels.Keys.First())));
        configuration.Groups[0].Name = "Changed outside the session";
        var original = Assert.Single(session.SavedGroups);
        Assert.Equal("Patch", original.Name);
        preferences!.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => session.SaveGroupAsync("Patch", [preferences.Id], false, true));
        Assert.Same(original, Assert.Single(session.SavedGroups));
        preferences.FailSave = false;
        await session.SaveGroupAsync("Patch", [preferences.Id], false, true);
        var saved = Assert.Single(session.SavedGroups);
        Assert.True(saved.OneWay);
        Assert.False(saved.SavedEnabled);
        Assert.Equal(preferences.Id, Assert.Single(saved.Members));
        Assert.Empty(original.Members);
        await Assert.ThrowsAsync<ArgumentException>(() => session.SaveGroupAsync("Patch", [preferences.Id], true, false));
        Assert.Same(saved, Assert.Single(session.SavedGroups));
    }

    [Fact]
    public async Task ManualAudioOptionsRestoreAndOnlyPublishSuccessfulSaves()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        Preferences? preferences = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (prepared, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Preferences: preferences = new(prepared.Channels.Keys.First())));
        Assert.True(session.CanSaveManualTransmitOptions);
        Assert.Equal(new ConsoleManualTransmitOptions(true, false), session.ManualTransmitOptions);
        preferences!.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => session.SetManualTransmitOptionsAsync(new(false, true)).AsTask());
        Assert.Equal(new ConsoleManualTransmitOptions(true, false), session.ManualTransmitOptions);
        preferences.FailSave = false;
        await session.SetManualTransmitOptionsAsync(new(false, true));
        Assert.Equal(new ConsoleManualTransmitOptions(false, true), session.ManualTransmitOptions);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetManualTransmitOptionsAsync(new(true, false)).AsTask());
    }

    [Fact]
    public async Task MicrophoneProcessingNormalizesDspWithoutChangingTheHostRoute()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        Preferences? preferences = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (prepared, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Preferences: preferences = new(prepared.Channels.Keys.First()),
                ManualInput: new AudioInputProcessingOptions { DeviceId = "host-input" }));
        Assert.Equal(1.5, session.MicrophoneProcessing.Gain);
        Assert.True(session.MicrophoneProcessing.AgcEnabled);
        var before = session.MicrophoneProcessing;
        preferences!.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => session.SetMicrophoneProcessingAsync(new()).AsTask());
        Assert.Same(before, session.MicrophoneProcessing);
        preferences.FailSave = false;
        await session.SetMicrophoneProcessingAsync(new()
        {
            DeviceId = "external-device",
            ProcessingMode = AudioProcessingMode.WindowsCommunications,
            Gain = 100,
            AgcTargetDbfs = double.NaN,
            LowGainDb = -20
        });
        Assert.Equal("host-input", session.MicrophoneProcessing.DeviceId);
        Assert.Equal(AudioProcessingMode.DvmConsole, session.MicrophoneProcessing.ProcessingMode);
        Assert.Equal(4, session.MicrophoneProcessing.Gain);
        Assert.Equal(-12, session.MicrophoneProcessing.LowGainDb);
        Assert.Equal(-25, session.MicrophoneProcessing.AgcTargetDbfs);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SetMicrophoneProcessingAsync(new()).AsTask());
    }

    private sealed class Preferences(ChannelId id) : IConsoleReceivePreferences, IConsoleListeningStartupPreferences, IConsoleTransmitPreferences, IConsoleGroupPreferences, IConsoleManualTransmitOptionsStore, IConsoleMicrophoneProcessingStore, IConsoleDiagnosticPreferences
    {

        public bool Verbose { get; set; } = true;
        public ValueTask<bool> LoadVerboseLoggingAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Verbose);
        public ValueTask SaveVerboseLoggingAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new IOException("Storage unavailable");
            Verbose = enabled;
            return ValueTask.CompletedTask;
        }
        public ValueTask<AudioInputProcessingOptions> LoadMicrophoneProcessingAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AudioInputProcessingOptions { Gain = 1.5, AgcEnabled = true });
        public ValueTask SaveMicrophoneProcessingAsync(AudioInputProcessingOptions options, CancellationToken cancellationToken = default)
            => FailSave ? ValueTask.FromException(new IOException("Storage unavailable")) : ValueTask.CompletedTask;
        public ValueTask<ConsoleManualTransmitOptions> LoadManualTransmitOptionsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ConsoleManualTransmitOptions(true, false));
        public ValueTask SaveManualTransmitOptionsAsync(ConsoleManualTransmitOptions options, CancellationToken cancellationToken = default)
            => FailSave ? ValueTask.FromException(new IOException("Storage unavailable")) : ValueTask.CompletedTask;
        public ValueTask<ConsoleGroupPreferences> LoadGroupsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ConsoleGroupPreferences(new(), false));
        public ValueTask SaveGroupAsync(string name, IReadOnlyList<DvmConsole.Core.Settings.PatchMemberSetting> members,
            bool enabled, bool oneWay, CancellationToken cancellationToken = default)
            => FailSave ? ValueTask.FromException(new IOException("Storage unavailable")) : ValueTask.CompletedTask;
        public ValueTask SaveRestorePatchesAsync(bool restore, CancellationToken cancellationToken = default)
            => FailSave ? ValueTask.FromException(new IOException("Storage unavailable")) : ValueTask.CompletedTask;
        public ValueTask<global::System.Collections.Immutable.ImmutableDictionary<ChannelId, ChannelTransmitPreferences>> LoadTransmitAsync(CancellationToken token)
            => ValueTask.FromResult(global::System.Collections.Immutable.ImmutableDictionary<ChannelId, ChannelTransmitPreferences>.Empty
                .Add(id, new(Selected: true)));
        public ValueTask SaveTransmitAsync(ChannelId channel, ChannelTransmitPreferenceChange change, CancellationToken token)
            => FailSave ? ValueTask.FromException(new IOException("Storage unavailable")) : ValueTask.CompletedTask;
        public ValueTask<bool> LoadRestoreSelectedChannelsAsync(CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask SaveRestoreSelectedChannelsAsync(bool restore, CancellationToken token)
            => FailSave ? ValueTask.FromException(new IOException("Storage unavailable")) : ValueTask.CompletedTask;
        public ChannelId Id => id;
        public bool FailSave { get; set; }
        public ChannelReceivePreferenceChange? LastChange { get; private set; }
        public ValueTask<global::System.Collections.Immutable.ImmutableDictionary<ChannelId, ChannelReceivePreferences>> LoadAsync(CancellationToken token)
            => ValueTask.FromResult(global::System.Collections.Immutable.ImmutableDictionary<ChannelId, ChannelReceivePreferences>.Empty
                .Add(id, new(0.75, -0.5, ReceiveEnabled: true, IgnoredSubscriberIds: [42])));
        public ValueTask SaveAsync(ChannelId channel, ChannelReceivePreferenceChange change, CancellationToken token)
        {
            if (FailSave) throw new IOException("Storage unavailable");
            LastChange = change;
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("restore")]
    [InlineData("toggle")]
    public async Task DisconnectCancelsActiveAndQueuedConnectionIntent(string command)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Radio("First")
        {
            Start = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }
        };
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        Task connecting = session.ConnectAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task queued = command switch
        {
            "restore" => session.RestoreAsync([first.SystemId]),
            "toggle" => session.ToggleAsync(first.SystemId),
            _ => session.ConnectAsync().AsTask()
        };
        await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, first.Starts);
        release.SetResult();
        await session.ConnectAsync();
        Assert.Equal(2, first.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementClosesRuntimeAdmissionBeforeWaitingForManualRelease(bool failDuringRelease)
    {
        var first = new Radio("First") { TransmitAvailable = true };
        int stops = 0;
        first.Stop = _ => { stops++; first.TransmitAvailable = false; return Task.CompletedTask; };
        first.Start = _ => { first.TransmitAvailable = true; return Task.CompletedTask; };
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var services = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        var runtime = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(services, plan.Systems, new PassthroughNormalizer(), ManualInput: new AudioInputProcessingOptions()));
        var application = new ConsoleApplicationSession(runtime);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool releaseFailed = false;
        var previous = new AdmissionAttachment(application, runtime, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            if (failDuringRelease && !releaseFailed)
            {
                releaseFailed = true;
                throw new IOException("Manual release failed.");
            }
        });
        var replacement = new AdmissionAttachment(new ConsoleApplicationSession(new ConsoleTopologySnapshot(null, [], [], []),
            ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands()));
        await using var owner = new ConsoleSessionHost(previous,
            candidate => { if (ReferenceEquals(candidate, replacement)) throw new IOException("Publication failed."); },
            () => { }, () => { });
        ChannelId channel = Assert.Single(runtime.CaptureTopology().Channels).Id;
        Task replacing = owner.ReplaceAsync(replacement);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(runtime.IsAcceptingCommands);
            Assert.False(await runtime.BeginPttAsync(channel));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.SetReceiveEnabledAsync(channel, true).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.SetChannelGainAsync(channel, 0.5).AsTask());
            first.Emit(new TrafficFrame(), [channel]);
            Assert.Empty(runtime.History);
            Assert.Equal(0, stops);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => replacing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(previous, owner.Active);
        Assert.True(runtime.IsAcceptingCommands);
        Assert.True(runtime.Execution.Snapshot.CanReceive);
        Assert.False(runtime.IsSelectedPttRequested);
        Assert.Equal(failDuringRelease ? 0 : 1, stops);
        await runtime.SetChannelGainAsync(channel, 0.75);
        first.Emit(new TrafficFrame(), [channel]);
        Assert.Equal(ChannelRuntimeState.Receiving, runtime.CaptureSnapshot().Channels[channel].RuntimeState);
        Assert.Single(runtime.History);
    }

    private sealed class AdmissionAttachment(ConsoleApplicationSession application,
        ConsoleReceiveSession? runtime = null, Func<CancellationToken, Task>? releaseManual = null) : IConsoleSessionAttachment
    {
        public IConsoleApplicationSession ApplicationSession => application;
        public void InitializeBindings() { }
        public void DetachBindings() { }
        public Task PrepareAsync(CancellationToken token) => Task.CompletedTask;
        public Task StartManualInputsAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopManualInputsAsync(CancellationToken token) => releaseManual?.Invoke(token) ?? Task.CompletedTask;
        public void StopAdmission() => application.SuspendInput();
        public void ResumeAdmission() { }
        public void SuppressLiveOutput() { }
        public IReadOnlyList<SystemId> CaptureActiveSystemIds() => runtime?.CaptureActiveSystemIds() ?? [];
        public ValueTask RestoreConnectionsAsync(IReadOnlyList<SystemId> systems, CancellationToken token)
            => new(runtime?.RestoreAsync(systems, token) ?? Task.CompletedTask);
        public Task RestorePlaybackAsync() => Task.CompletedTask;
        public ValueTask DisposeSessionAsync() => application.DisposeAsync();
        public ValueTask DisposeManualControlsAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeOwnerAsync() => application.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryJoinsPendingWebCleanupBeforeRestoringNativeAudio(bool superseded)
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        var preferences = new WebCleanupPreferences();
        var configuration = Configuration();
        configuration.Zones[0].WebStreams = [new() { Name = "Feed", Url = "https://example.invalid/feed" }];
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), Preferences: preferences));
        await session.ResumeAudioAsync(_ => Task.CompletedTask);
        WebStreamId stream = Assert.Single(session.WebStreams).Id;
        Task saving = session.SetWebStreamVolumeAsync(stream, 0.5);
        await preferences.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.SetAudioAvailable(false, "Interrupted");
        int restores = 0;
        Task recovery = session.ResumeAudioAsync(_ =>
        {
            Assert.True(preferences.SaveCompleted, "Native restoration ran before outgoing web cleanup could finish.");
            restores++;
            return Task.CompletedTask;
        });
        try
        {
            if (superseded) session.SetAudioAvailable(false, "New interruption");
        }
        finally
        {
            preferences.ReleaseSave.TrySetResult();
        }
        await saving.WaitAsync(TimeSpan.FromSeconds(10));
        if (superseded)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, restores);
            Assert.Equal("New interruption", session.CaptureSnapshot().StatusText);
        }
        else
        {
            await recovery.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, restores);
            Assert.Contains("Listening resumed", session.CaptureSnapshot().StatusText);
        }
    }

    private sealed class WebCleanupPreferences : IConsoleReceivePreferences, IConsoleWebStreamPreferences
    {
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SaveCompleted { get; private set; }
        public ValueTask<global::System.Collections.Immutable.ImmutableDictionary<ChannelId, ChannelReceivePreferences>> LoadAsync(CancellationToken token)
            => ValueTask.FromResult(global::System.Collections.Immutable.ImmutableDictionary<ChannelId, ChannelReceivePreferences>.Empty);
        public ValueTask SaveAsync(ChannelId id, ChannelReceivePreferenceChange change, CancellationToken token)
            => ValueTask.CompletedTask;
        public ValueTask<global::System.Collections.Immutable.ImmutableDictionary<WebStreamId, ConsoleWebStreamPreference>> LoadWebStreamsAsync(
            IReadOnlyList<WebStreamPlaybackDescriptor> streams, CancellationToken token = default)
            => ValueTask.FromResult(global::System.Collections.Immutable.ImmutableDictionary<WebStreamId, ConsoleWebStreamPreference>.Empty);
        public async ValueTask SaveWebStreamAsync(WebStreamPlaybackDescriptor stream, bool? selected = null,
            double? volume = null, CancellationToken cancellationToken = default)
        {
            SaveEntered.TrySetResult();
            await ReleaseSave.Task.WaitAsync(cancellationToken);
            SaveCompleted = true;
        }
    }

    [Fact]
    public async Task NewInterruptionSupersedesRecoveryBeforeItPublishesListening()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        session.SetAudioAvailable(false, "First interruption");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task recovery = session.ResumeAudioAsync(async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.SetAudioAvailable(false, "New interruption");
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        Assert.Equal("New interruption", session.CaptureSnapshot().StatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryFailureCanOnlyChangeItsOwnInterruption(bool superseded)
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        session.SetAudioAvailable(false, "First interruption");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task recovery = session.ResumeAudioAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
            throw new IOException("Endpoint recreation failed");
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (superseded) session.SetAudioAvailable(false, "New interruption");
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => recovery);
        Assert.Equal(superseded ? "New interruption"
            : "Listening recovery failed: Endpoint recreation failed Open Settings to resume.",
            session.CaptureSnapshot().StatusText);
        await session.ResumeAudioAsync(_ => Task.CompletedTask);
        Assert.Contains("Listening resumed", session.CaptureSnapshot().StatusText);
    }

    [Fact]
    public async Task PauseInvalidatesRecoveryAlreadyWaitingForCommandOwnership()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SetAudioAvailable(false, "Interrupted");
        Task first = session.ResumeAudioAsync(async _ => { entered.SetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        int queuedRestores = 0;
        Task queued = session.ResumeAudioAsync(_ => { queuedRestores++; return Task.CompletedTask; });
        session.SetAudioAvailable(false, "Paused by operator", requiresExplicitResume: true);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, queuedRestores);
        Assert.Equal(ConsoleExecutionState.RequiresResume, session.Execution.Snapshot.State);
        Assert.Equal("Paused by operator", session.CaptureSnapshot().StatusText);
    }

    [Fact]
    public async Task SessionExecutionOwnerPreservesBackgroundAndExplicitResumeState()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        ConsoleSessionState? state = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (prepared, _) =>
        {
            state = prepared;
            return new(host, plan.Systems, new PassthroughNormalizer());
        });
        Assert.Same(state!.Execution, session.Execution);
        session.Execution.SetForeground(false);
        session.SetAudioAvailable(false, "Audio reset", requiresExplicitResume: true);
        Assert.Equal(ConsoleExecutionState.RequiresResume, session.Execution.Snapshot.State);
        Assert.Null(session.Execution.BeginRecovery(explicitResume: false));
        await session.ResumeAudioAsync(_ => Task.CompletedTask);
        Assert.Equal(ConsoleExecutionState.BackgroundListening, session.Execution.Snapshot.State);
        Assert.Null(session.Execution.TryAcquire(ConsoleTransmitIntent.Manual));
        session.Execution.SetForeground(true);
        session.Execution.SetManualControlsAvailable(false);
        Assert.Null(session.Execution.TryAcquire(ConsoleTransmitIntent.Manual));
        Assert.True(session.Execution.Snapshot.CanReceive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementRollbackPreservesUnavailableAudio(bool requiresExplicitResume)
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        session.Execution.SetForeground(false);
        session.SetAudioAvailable(false, "Listening paused", requiresExplicitResume);
        Assert.True(session.IsAcceptingCommands);
        await session.QuiesceAsync(CancellationToken.None);
        Assert.False(session.IsAcceptingCommands);
        session.ReactivateAfterFailedReplacement();
        Assert.True(session.IsAcceptingCommands);
        Assert.Equal(requiresExplicitResume ? ConsoleExecutionState.RequiresResume : ConsoleExecutionState.Interrupted,
            session.Execution.Snapshot.State);
        Assert.False(session.Execution.Snapshot.CanReceive);
        Assert.Null(session.Execution.TryAcquire(ConsoleTransmitIntent.AutomaticPatch));
        Assert.Equal("Listening paused", session.CaptureSnapshot().StatusText);
        await session.ResumeAudioAsync(_ => Task.CompletedTask);
        Assert.Equal(ConsoleExecutionState.BackgroundListening, session.Execution.Snapshot.State);
        Assert.True(session.Execution.Snapshot.CanReceive);
    }

    [Fact]
    public async Task RetiringReceiveSessionCancelsRecoveryAndCannotReplayLateResume()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SetAudioAvailable(false, "Interrupted");
        Task recovery = session.ResumeAudioAsync(async token =>
        {
            entered.SetResult();
            await released.Task; // A native completion may arrive after cancellation.
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task stopping = session.QuiesceAsync(CancellationToken.None).AsTask();
        released.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.WaitAsync(TimeSpan.FromSeconds(10)));
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Interrupted", session.CaptureSnapshot().StatusText);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ResumeAudioAsync(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task ReceiveMetersUseSharedBallisticsAndDiscardInterruptedReadings()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var scheduler = new DormantScheduler();
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, scheduler, SystemApplicationDelay.Instance, null!, []);
        ConsoleSessionState state = null!;
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (prepared, _) =>
        { state = prepared; return new(host, plan.Systems, new PassthroughNormalizer()); });
        ChannelId id = state.Channels.Keys.Single();
        state.Channels[id].Operator.SetAudioEnabled(true);
        var samples = new List<ChannelMeterSample>();
        session.MeterSampled += (_, _) => throw new InvalidOperationException("broken meter observer");
        session.MeterSampled += (_, sample) => samples.Add(sample);
        session.ObservePresentedSamples(id, 10, Enumerable.Repeat((short)32767, 400).ToArray(), TimeSpan.Zero);
        Assert.Equal(1, scheduler.Starts);
        session.AdvanceMeters();
        Assert.InRange(Assert.Single(samples).Peak, 99, 100);
        session.SetAudioAvailable(false, "Interrupted");
        Assert.Equal(0, samples[^1].Peak);
        Assert.Equal(1, scheduler.Stops);
        session.SetAudioAvailable(true, "Recovered");
        int previous = samples.Count;
        session.AdvanceMeters();
        Assert.Equal(previous, samples.Count);
        state.Channels[id].Operator.SetAudioEnabled(false);
        session.ObservePresentedSamples(id, 11, new short[400], TimeSpan.Zero);
        session.AdvanceMeters();
        Assert.Equal(previous, samples.Count);
    }

    [Fact]
    public async Task ReceiveSessionProjectsTrafficThroughSharedRoutingDespiteFaultingObservers()
    {
        var radio = new Radio("First");
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(radio)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        session.ControlStateInvalidated += (_, _) => throw new InvalidOperationException("broken view");
        session.LogPublished += (_, _) => throw new InvalidOperationException("broken log view");
        var frame = new TrafficFrame();
        radio.Emit(frame, session.CaptureTopology().Channels.Select(channel => channel.Id).ToArray());
        var snapshot = Assert.Single(session.CaptureSnapshot().Channels).Value;
        Assert.Equal(ChannelRuntimeState.Receiving, snapshot.RuntimeState);
        Assert.Equal("42", snapshot.LastCaller);
        Assert.Single(session.History);
        await session.DisposeAsync();
        radio.Emit(frame, session.CaptureTopology().Channels.Select(channel => channel.Id).ToArray());
        Assert.Single(session.History);
    }

    [Fact]
    public async Task ClearingSessionHistoryKeepsReceiveAdmissionAndDoesNotRetireRadios()
    {
        var radio = new Radio("First");
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(radio)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer()));
        var channels = session.CaptureTopology().Channels.Select(channel => channel.Id).ToArray();
        radio.Emit(new TrafficFrame(), channels);
        Assert.Single(session.History);
        await ((IConsoleHistoryCommands)session.Commands).ClearSessionHistoryAsync();
        Assert.Empty(session.History);
        Assert.Equal(ChannelRuntimeState.Receiving, Assert.Single(session.CaptureSnapshot().Channels).Value.RuntimeState);
        radio.Emit(new TrafficFrame(), channels);
        Assert.Single(session.History);
    }

    [Fact]
    public async Task ReceiveAliasesRemainSystemSpecificAndFrozenBeforeHostPreparation()
    {
        var configuration = Configuration();
        configuration.Systems[0].RidAlias = [new RadioAlias { Rid = 42, Alias = "First caller" }];
        configuration.Systems[1].RidAlias = [new RadioAlias { Rid = 42, Alias = "Second caller" }];
        configuration.Zones[0].Channels.Add(new ChannelConfiguration { Name = "Secondary", System = "Second", Mode = "p25", Tgid = "100" });
        var first = new Radio("First");
        var second = new Radio("Second");
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(second))]);
        var host = new ConsoleHostServices(plan, null!, null!, null!, null!, null!, null!,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (_, _) =>
        {
            configuration.Systems[0].RidAlias[0].Alias = "Changed during preparation";
            return new(host, plan.Systems, new PassthroughNormalizer());
        });
        var channels = session.CaptureTopology().Channels;
        first.Emit(new TrafficFrame(), channels.Where(channel => channel.SystemId == first.SystemId).Select(channel => channel.Id).ToArray());
        second.Emit(new TrafficFrame(), channels.Where(channel => channel.SystemId == second.SystemId).Select(channel => channel.Id).ToArray());
        var snapshot = session.CaptureSnapshot();
        Assert.Equal("First caller", snapshot.Channels[channels.Single(channel => channel.SystemId == first.SystemId).Id].LastCaller);
        Assert.Equal("Second caller", snapshot.Channels[channels.Single(channel => channel.SystemId == second.SystemId).Id].LastCaller);
    }

    private sealed class TrafficFrame : IRadioMediaFrame
    {
        public RadioMediaProtocol Protocol => RadioMediaProtocol.P25;
        public uint PeerId => 1;
        public uint SourceId => 42;
        public uint DestinationId => 100;
        public byte? Slot => null;
        public string CallType => "GROUP";
        public string FrameType => "VOICE";
        public string Subtype => "LDU1";
        public ushort PacketSequence => 1;
        public uint StreamId => 10;
        public byte[] Payload => [];
    }

    private sealed class PassthroughNormalizer : IRadioReceiveFrameNormalizer
    { public IRadioMediaFrame? Normalize(IRadioMediaFrame frame) => frame; }
    private sealed class DormantScheduler : IApplicationScheduler
    {
        private readonly List<(TimeSpan Interval, Func<CancellationToken, ValueTask> Callback)> callbacks = [];
        public int Disposals;
        public int Starts;
        public int Stops;
        public IScheduledWork CreatePeriodic(TimeSpan interval, Func<CancellationToken, ValueTask> callback, bool startImmediately = true)
        {
            callbacks.Add((interval, callback));
            return new Work(this);
        }
        public async Task TickAsync(TimeSpan interval)
        {
            foreach (var work in callbacks.Where(work => work.Interval == interval))
                await work.Callback(CancellationToken.None);
        }
        private sealed class Work(DormantScheduler owner) : IScheduledWork
        {
            public bool IsRunning => false;
            public void Start() => owner.Starts++;
            public void Stop() => owner.Stops++;
            public ValueTask DisposeAsync() { owner.Disposals++; return ValueTask.CompletedTask; }
        }
    }

    private static ConsoleRadioSessionBinding Binding(string name, Func<CancellationToken, ValueTask<IRadioSession>> create)
        => new(new RadioSystemDescriptor(SystemId.FromName(name), name, "FNE", new Dictionary<string, string>()), new Factory(create));

    [Fact]
    public async Task CancellingAlertPreparationJoinsLateOpenAndDisposesItsStream()
    {
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(new Radio("First"))),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var assets = new HeldAssetStore();
        var host = new ConsoleHostServices(plan, null!, null!, null!, assets, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        await using var session = await ConsoleReceiveSession.CreateAsync(Configuration(), (_, _) =>
            new(host, plan.Systems, new PassthroughNormalizer(), ManualInput: new AudioInputProcessingOptions()));
        Task sending = session.SendAlertAudioAsync(AssetId.New());
        await assets.Entered.Task;
        Task cancellation = session.CancelTonesAsync();
        Assert.False(cancellation.IsCompleted);
        var stream = new TrackedAssetStream();
        assets.Opened.SetResult(stream);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await cancellation;
        Assert.True(stream.Disposed);
        Assert.All(session.CaptureSnapshot().Channels.Values, channel => Assert.False(channel.Transmitting));
    }

    private sealed class TrackedAssetStream : MemoryStream
    {
        public bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class HeldAssetStore : IAssetStore
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Stream> Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<Stream> OpenReadAsync(AssetId id, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); return new(Opened.Task); }
        public ValueTask<AssetDescriptor> ImportAsync(string displayName, string mediaType, Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<AssetDescriptor> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }

    [Fact]
    public async Task AuthorityWithdrawalStopsAnActiveManualCallOnlyWhenItsTargetIsAffected()
    {
        var first = new Radio("First") { TransmitAvailable = true };
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var audio = new HeldCaptureBackend(emitSamples: true);
        audio.Release.TrySetResult();
        var host = new ConsoleHostServices(plan, audio, null!, null!, null!, null!, new Lifecycle(),
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        var configuration = Configuration();
        configuration.Zones[0].Channels[0].Mode = "analog";
        configuration.Zones[0].Channels.Add(new ChannelConfiguration
        { Name = "Other", System = "First", Mode = "analog", Tgid = "101" });
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (prepared, _) =>
        {
            first.ChannelIds = prepared.Channels.Keys.ToArray();
            return new(host, plan.Systems, new PassthroughNormalizer(), ManualInput: new AudioInputProcessingOptions());
        });
        var channels = session.CaptureTopology().Channels;
        ChannelId id = channels[0].Id;
        Assert.True(await session.BeginPttAsync(id), session.CaptureSnapshot().StatusText);
        first.PublishAuthority(new(first.SystemId, [new(channels[1].Id, TargetAuthorityState.Unavailable, null)], DateTimeOffset.UtcNow));
        Assert.True(session.CaptureSnapshot().Channels[id].Transmitting);

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ControlStateInvalidated += (_, _) =>
        {
            if (!session.CaptureSnapshot().Channels[id].Transmitting) stopped.TrySetResult();
        };
        first.Authority = TargetAuthorityState.Unavailable;
        first.PublishAuthority(new(first.SystemId, [new(id, first.Authority, null)], DateTimeOffset.UtcNow));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.CaptureSnapshot().Channels[id].Transmitting);
        Assert.False(await session.BeginPttAsync(id));
    }

    [Theory]
    [InlineData("authority", false)]
    [InlineData("authority", true)]
    [InlineData("release", false)]
    [InlineData("background", false)]
    [InlineData("interruption", false)]
    [InlineData("release", true)]
    [InlineData("background", true)]
    [InlineData("interruption", true)]
    public async Task ComposedManualStartupIsRevokedWithoutAnActiveCall(string reason, bool selected)
    {
        var first = new Radio("First") { TransmitAvailable = true };
        var plan = new ConsoleRadioSessionPlan([
            Binding("First", _ => ValueTask.FromResult<IRadioSession>(first)),
            Binding("Second", _ => ValueTask.FromResult<IRadioSession>(new Radio("Second")))]);
        var audio = new HeldCaptureBackend();
        var lifecycle = new Lifecycle();
        var host = new ConsoleHostServices(plan, audio, null!, null!, null!, null!, lifecycle,
            SystemClock.Instance, new DormantScheduler(), SystemApplicationDelay.Instance, null!, []);
        var configuration = Configuration();
        configuration.Zones[0].Channels[0].Mode = "analog";
        if (selected) configuration.Zones[0].Channels.Add(new ChannelConfiguration
        { Name = "Second target", System = "First", Mode = "analog", Tgid = "101" });
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (prepared, _) =>
        {
            first.ChannelIds = prepared.Channels.Keys.ToArray();
            return new(host, plan.Systems, new PassthroughNormalizer(), ManualInput: new AudioInputProcessingOptions());
        });
        ChannelId id = session.CaptureTopology().Channels[0].Id;
        Assert.False(session.CaptureTopology().Channels[0].ReceiveOnly);
        if (selected)
            foreach (var channel in session.CaptureTopology().Channels) await session.SetTransmitSelectedAsync(channel.Id, true);
        Task<bool> press = selected ? session.BeginSelectedPttAsync().AsTask() : session.BeginPttAsync(id).AsTask();
        try
        {
            Task ready = await Task.WhenAny(audio.Entered.Task, press).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(ReferenceEquals(ready, audio.Entered.Task), session.CaptureSnapshot().StatusText);
            if (selected)
            {
                Assert.True(session.IsSelectedPttRequested);
                Assert.All(session.CaptureSnapshot().Channels.Values, channel => Assert.True(channel.TransmitStarting));
                await session.EndPttAsync(id);
                Assert.False(press.IsCompleted);
                await session.SetTransmitSelectedAsync(id, false);
            }
            Task Release() => selected ? session.EndSelectedPttAsync(new CancellationToken(true)).AsTask()
                : session.EndPttAsync(id, new CancellationToken(true)).AsTask();
            Task release;
            if (reason == "background") { lifecycle.Background(); lifecycle.Background(); release = Release(); }
            else if (reason == "interruption") { session.SetAudioAvailable(false, "Interrupted"); release = Release(); }
            else if (reason == "authority")
            {
                first.Authority = TargetAuthorityState.Unavailable;
                first.PublishAuthority(new(first.SystemId, [new(id, first.Authority, "Unavailable")], DateTimeOffset.UtcNow));
                release = Task.CompletedTask;
            }
            else release = Release();
            Assert.False(await press.WaitAsync(TimeSpan.FromSeconds(30)));
            await release.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(audio.Disposed);
            Assert.False(session.CaptureSnapshot().Channels[id].TransmitStarting);
            Assert.False(session.CaptureSnapshot().Channels[id].Transmitting);
            if (reason == "interruption") Assert.Equal("Interrupted", session.CaptureSnapshot().StatusText);
            if (reason != "release") Assert.False(await session.BeginPttAsync(id));
        }
        finally { audio.Release.TrySetResult(); }
    }

    private sealed class Lifecycle : IApplicationLifecycle
    {
        public bool IsActive { get; private set; } = true;
        public event EventHandler? Activated { add { } remove { } }
        public event EventHandler? Deactivated;
        public event EventHandler? Suspending { add { } remove { } }
        public event EventHandler? Resumed { add { } remove { } }
        public event EventHandler? Stopping { add { } remove { } }
        public void Background() { IsActive = false; Deactivated?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class HeldCaptureBackend(bool emitSamples = false) : IAudioBackendFactory, IAudioBackend, IAudioCapture
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public IAudioBackend Create(AudioBackendConfiguration configuration) => this;
        public string Name => "Held capture";
        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => [new("default", "Synthetic", direction, true, false)];
        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format) => this;
        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format) => throw new NotSupportedException();
        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        private Timer? samplesTimer;
        private readonly short[] samples = new short[160];
        public bool IsRunning { get; private set; }
        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public async ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            IsRunning = true;
            if (emitSamples)
                samplesTimer = new Timer(_ => SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples)),
                    null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        { IsRunning = false; samplesTimer?.Dispose(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void Dispose() { IsRunning = false; samplesTimer?.Dispose(); Disposed = true; }
    }

    private sealed class Factory(Func<CancellationToken, ValueTask<IRadioSession>> create) : IRadioSessionFactory
    {
        public ValueTask<IRadioSession> CreateAsync(RadioSystemDescriptor system, CancellationToken cancellationToken = default)
            => create(cancellationToken);
    }

    private static ConsoleSessionState State() => ConsoleSessionState.Create(Configuration());

    private static ConsoleConfiguration Configuration() => new ConsoleConfiguration
    {
        Systems = [System("First", 1), System("Second", 2)],
        Zones = [new ZoneConfiguration { Name = "Dispatch", Channels =
            [new ChannelConfiguration { Name = "Operations", System = "First", Mode = "p25", Tgid = "100" }] }]
    };

    private static SystemConfiguration System(string name, uint peer) => new()
    {
        Name = name,
        Identity = "Console",
        Address = "127.0.0.1",
        Port = 62031,
        PeerId = peer,
        Rid = "1001"
    };

    private sealed class Radio(string name) : IRadioSession, IRadioSessionAbort, IRadioSubscriberCommandEndpoint, IRadioP25KeyEndpoint, IRadioConnectionStateNotifications, IRadioDiagnosticSettings
    {

        public bool Verbose { get; private set; }
        public void SetVerboseLogging(bool enabled) => Verbose = enabled;
        public List<(ConsoleSubscriberCommand Command, uint Destination)> SubscriberCommands { get; } = [];
        public Exception? SubscriberSendFailure { get; set; }
        public void SendSubscriberCommand(ConsoleSubscriberCommand command, uint destinationId)
        {
            if (SubscriberSendFailure is { } failure) throw failure;
            SubscriberCommands.Add((command, destinationId));
        }
        public event EventHandler<RadioP25KeyResponse>? P25KeyReceived;
        public event EventHandler? ConnectionStateChanged;
        public RadioConnectionSnapshot ConnectionState => new(SystemId, Name,
            IsConnected ? RadioConnectionState.Connected : RadioConnectionState.Disconnected, string.Empty, DateTimeOffset.UtcNow);
        public Action<byte, ushort>? RequestKey { get; set; }
        public void RequestP25Key(byte algorithm, ushort key) => RequestKey?.Invoke(algorithm, key);
        public void PublishKey(RadioP25KeyResponse response) => P25KeyReceived?.Invoke(this, response);
        public void PublishConnection() => ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
        public int AbortCount;
        public void Abort() => AbortCount++;
        public int DisposeCount;
        public int Starts;
        public Func<CancellationToken, Task>? Start;
        public Func<CancellationToken, Task>? Stop;
        public SystemId SystemId { get; } = SystemId.FromName(name);
        public string Name => name;
        public TargetAuthorityState Authority { get; set; } = TargetAuthorityState.Available;
        public bool TransmitAvailable { get; set; }
        public bool IsConnected => TransmitAvailable;
        public bool IsConnectionActive => TransmitAvailable;
        public uint? SourceId => TransmitAvailable ? 1001u : null;
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => [];
        public IReadOnlyCollection<ChannelId> ChannelIds { get; set; } = [];
        public event EventHandler<RadioTrafficRecord>? TrafficReceived;
        public void Emit(IRadioMediaFrame frame, IReadOnlyList<ChannelId> candidates) => TrafficReceived?.Invoke(this,
            new RadioTrafficRecord(SystemId, candidates, frame, DateTimeOffset.UtcNow));
        public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged;
        public void PublishAuthority(TalkgroupAuthorityRecord record) => AuthorityChanged?.Invoke(this, record);
        public ValueTask StartAsync(CancellationToken cancellationToken = default) { Starts++; return new(Start?.Invoke(cancellationToken) ?? Task.CompletedTask); }
        public ValueTask QuiesceAsync(CancellationToken cancellationToken = default) => new(Stop?.Invoke(cancellationToken) ?? Task.CompletedTask);
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
        public TargetAuthorityState GetTargetAuthority(RadioMediaProtocol protocol, uint destinationId, byte runtimeSlot)
            => Authority;
        public uint CreateStreamId() => 1;
        public void SendTraffic(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort packetSequence, uint streamId) { }
    }
}
