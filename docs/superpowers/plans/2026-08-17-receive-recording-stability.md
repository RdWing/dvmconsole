# Receive and Recording Stability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep concurrent receive streams isolated, recover selected receive audio when shared output infrastructure fails, preserve active TX/RX while warm microphone capture changes, prevent late traffic from creating duplicate calls, preserve stream identity through decoded audio, and finalize playable recordings without blocking receive work.

**Architecture:** Introduce a per-channel receive lifecycle state machine that distinguishes active, timeout-grace, and hard-terminated streams. Carry immutable stream/source context through audio callbacks, publish only changed diagnostics, and hand finalized WAV snapshots to a single background finalization queue. Main-window history and recording updates consume explicit lifecycle/finalization results instead of inferring them from mutable channel state.

**Tech Stack:** .NET 10, C#, Avalonia dispatcher, `System.Threading.Channels`, existing DVM audio/vocoder boundaries, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-17-channel-history-stream-stability-design.md`

## Global Constraints

- BER reporting is out of scope.
- Different channels must continue decoding concurrently; one channel remains serialized.
- Explicit terminators hard-close a stream and late packets cannot reopen it.
- Timeout continuation within a two-second grace interval remains one logical call and recording.
- Recording identity must come from the decoded frame, not mutable `ChannelViewModel.StreamId`.
- Filesystem scanning, trimming, encoding, metadata writing, and validation must not run on the receive worker or UI thread.
- Invalid, empty, silent-only, missing, or undecodable output must not expose Play.
- Recoverable playback-route faults must not clear desired RX selections.
- Disabling warm microphone capture must not release active transmit leases or a shared macOS playback endpoint.
- Do not add a new runtime or NuGet dependency.

---

### Task 1: Model receive-stream lifecycle transitions

**Files:**
- Create: `src/DvmConsole.Desktop/ReceiveStreamLifecycle.cs`
- Test: `src/DvmConsole.Desktop.Tests/ReceiveStreamLifecycleTests.cs`

**Interfaces:**
- Produces: `ReceiveStreamLifecycle.ObserveVoice(uint streamId, DateTimeOffset now) -> ReceiveStreamDecision`.
- Produces: `ReceiveStreamLifecycle.ObserveTerminator(uint streamId, DateTimeOffset now) -> ReceiveStreamDecision`.
- Produces: `ReceiveStreamLifecycle.Advance(DateTimeOffset now) -> ReceiveStreamDecision`.
- Produces: `ReceiveStreamTransition` values `IgnoredLate`, `Started`, `Continued`, `Resumed`, `Ended`, `Superseded`, `GraceStarted`, `GraceExpired`, and `None`.

- [ ] **Step 1: Write failing lifecycle tests**

```csharp
[Fact]
public void ExplicitTerminationTombstonesLateVoice()
{
    var lifecycle = new ReceiveStreamLifecycle(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    DateTimeOffset now = DateTimeOffset.UnixEpoch;

    Assert.Equal(ReceiveStreamTransition.Started, lifecycle.ObserveVoice(7, now).Transition);
    Assert.Equal(ReceiveStreamTransition.Ended, lifecycle.ObserveTerminator(7, now.AddSeconds(1)).Transition);
    Assert.Equal(ReceiveStreamTransition.IgnoredLate, lifecycle.ObserveVoice(7, now.AddSeconds(2)).Transition);
}

[Fact]
public void TimeoutGraceResumesTheSameStream()
{
    var lifecycle = new ReceiveStreamLifecycle(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    DateTimeOffset now = DateTimeOffset.UnixEpoch;

    lifecycle.ObserveVoice(8, now);
    Assert.Equal(ReceiveStreamTransition.GraceStarted, lifecycle.Advance(now.AddSeconds(3)).Transition);
    Assert.Equal(ReceiveStreamTransition.Resumed, lifecycle.ObserveVoice(8, now.AddSeconds(3.5)).Transition);
    Assert.Equal((uint)8, lifecycle.ActiveStreamId);
}

[Fact]
public void NewStreamSupersedesOldStreamWithoutAllowingItBack()
{
    var lifecycle = new ReceiveStreamLifecycle(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    DateTimeOffset now = DateTimeOffset.UnixEpoch;

    lifecycle.ObserveVoice(10, now);
    ReceiveStreamDecision decision = lifecycle.ObserveVoice(11, now.AddMilliseconds(100));

    Assert.Equal(ReceiveStreamTransition.Superseded, decision.Transition);
    Assert.Equal((uint)10, decision.EndedStreamId);
    Assert.Equal((uint)11, decision.ActiveStreamId);
    Assert.Equal(ReceiveStreamTransition.IgnoredLate, lifecycle.ObserveVoice(10, now.AddSeconds(1)).Transition);
}
```

- [ ] **Step 2: Run tests and verify missing lifecycle types**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~ReceiveStreamLifecycleTests /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because the lifecycle types do not exist.

- [ ] **Step 3: Implement the state machine**

```csharp
internal enum ReceiveStreamTransition
{
    None,
    IgnoredLate,
    Started,
    Continued,
    Resumed,
    Ended,
    Superseded,
    GraceStarted,
    GraceExpired
}

internal readonly record struct ReceiveStreamDecision(
    ReceiveStreamTransition Transition,
    uint? ActiveStreamId = null,
    uint? EndedStreamId = null)
{
    public bool AcceptTraffic => Transition is not (ReceiveStreamTransition.None or ReceiveStreamTransition.IgnoredLate);
}
```

`ReceiveStreamLifecycle` stores one active stream, last activity, an `inGrace` flag/deadline, and `Dictionary<uint, DateTimeOffset>` tombstone expirations. Purge expired tombstones before each operation. A new stream tombstones and reports the old stream as `Superseded`. `Advance` first returns `GraceStarted`, then returns `GraceExpired` after the grace deadline and clears the active stream.

- [ ] **Step 4: Run all lifecycle tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~ReceiveStreamLifecycleTests /m:1 /p:UseSharedCompilation=false`

Expected: PASS, including exact-boundary cases at inactivity, grace, and tombstone expiration.

- [ ] **Step 5: Commit the state machine**

```bash
git add src/DvmConsole.Desktop/ReceiveStreamLifecycle.cs src/DvmConsole.Desktop.Tests/ReceiveStreamLifecycleTests.cs
git commit -m "feat: model receive stream lifecycle"
```

### Task 2: Integrate explicit lifecycle results into channels and History

**Files:**
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:4435-4550,6078-6125,7350-7420`
- Modify: `src/DvmConsole.Desktop/CallHistory.cs`
- Test: `src/DvmConsole.Desktop.Tests/ChannelViewModelTests.cs`
- Test: `src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs`
- Test: `src/DvmConsole.Desktop.Tests/CallHistoryStoreTests.cs`

**Interfaces:**
- Consumes: `ReceiveStreamLifecycle` from Task 1.
- Produces: `ChannelViewModel.ApplyTraffic(string systemName, FneTrafficFrame traffic, DateTimeOffset now) -> ChannelTrafficApplyResult`.
- Produces: `ChannelViewModel.AdvanceReceiveLifecycle(DateTimeOffset now) -> ChannelTrafficApplyResult`.

- [ ] **Step 1: Write failing integration tests**

Add tests proving:

```csharp
[Fact]
public void LateVoiceAfterTerminatorDoesNotReopenChannel()
{
    ChannelViewModel channel = CreateDmrChannel();
    DateTimeOffset now = DateTimeOffset.UnixEpoch;

    Assert.Equal(ReceiveStreamTransition.Started, channel.ApplyTraffic("System 1", Voice(7), now).Transition);
    Assert.Equal(ReceiveStreamTransition.Ended, channel.ApplyTraffic("System 1", Terminator(7), now.AddSeconds(1)).Transition);
    Assert.Equal(ReceiveStreamTransition.IgnoredLate, channel.ApplyTraffic("System 1", Voice(7), now.AddSeconds(2)).Transition);
    Assert.Equal(ChannelRuntimeState.Idle, channel.State);
}
```

Add a `SystemViewModelTests` sequence that sends voice, advances into grace, sends the same stream, and asserts one `CallHistoryEntry` remains active. Add a supersession test asserting the old History row completes once and only one new row is inserted.

- [ ] **Step 2: Run focused integration tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~LateVoiceAfterTerminator|FullyQualifiedName~TimeoutGrace|FullyQualifiedName~Supersed" /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because lifecycle results are not integrated.

- [ ] **Step 3: Return transitions from channel traffic application**

```csharp
internal readonly record struct ChannelTrafficApplyResult(
    bool Matched,
    ReceiveStreamTransition Transition,
    uint? ActiveStreamId = null,
    uint? EndedStreamId = null)
{
    public static ChannelTrafficApplyResult NoMatch => new(false, ReceiveStreamTransition.None);
}
```

Keep protocol/destination/slot/privacy validation in `ChannelViewModel`. For accepted voice, call `ObserveVoice`; for a matching terminator call `ObserveTerminator`; use the decision to call `runtime.MarkReceiving` or `runtime.MarkIdle`. Retain `TryApplyTraffic` as a compatibility wrapper returning `ApplyTraffic(..., DateTimeOffset.UtcNow).Matched` while production and updated tests use the explicit API.

In `MainWindowViewModel.ProcessTraffic`, replace `sameActiveStream` inference with transition handling:

```csharp
ChannelTrafficApplyResult applied = channel.ApplyTraffic(system.Name, traffic, DateTimeOffset.UtcNow);
if (!applied.Matched || applied.Transition == ReceiveStreamTransition.IgnoredLate)
    continue;

if (applied.EndedStreamId is uint ended)
    callHistory.Complete(system.Name, traffic.Protocol, ended, now);
if (applied.Transition is ReceiveStreamTransition.Started or ReceiveStreamTransition.Superseded)
    callHistory.Add(CreateHistoryEntry(channel, system, traffic, now));
```

`ExpireStaleReceiveStates` calls `AdvanceReceiveLifecycle`; `GraceStarted` changes no History/recording state, while `GraceExpired` completes the reported stream. Add `CallHistoryStore.ActiveCount`-oriented assertions rather than relying only on collection positions.

- [ ] **Step 4: Run channel, History, and system tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~ChannelViewModelTests|FullyQualifiedName~CallHistoryStoreTests|FullyQualifiedName~SystemViewModelTests" /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 5: Commit lifecycle integration**

```bash
git add src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop/CallHistory.cs src/DvmConsole.Desktop.Tests/ChannelViewModelTests.cs src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs src/DvmConsole.Desktop.Tests/CallHistoryStoreTests.cs
git commit -m "fix: keep late traffic in one call lifecycle"
```

### Task 3: Publish receive diagnostics only when counters change

**Files:**
- Create: `src/DvmConsole.Desktop/ReceiveDiagnosticsReporter.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:4690-4760`
- Test: `src/DvmConsole.Desktop.Tests/ReceiveDiagnosticsReporterTests.cs`

**Interfaces:**
- Produces: `ReceiveDiagnosticsReporter.ShouldPublish(ChannelViewModel channel, ReceiveAudioDiagnostics diagnostics, DateTimeOffset now) -> bool`.

- [ ] **Step 1: Write failing changed-counter tests**

```csharp
[Fact]
public void RepeatedCumulativeIssueIsNotRepublished()
{
    var reporter = new ReceiveDiagnosticsReporter(TimeSpan.FromMilliseconds(500));
    ChannelViewModel channel = CreateChannel();
    DateTimeOffset now = DateTimeOffset.UnixEpoch;
    var first = new ReceiveAudioDiagnostics(10, 1, 2, 0);

    Assert.True(reporter.ShouldPublish(channel, first, now));
    Assert.False(reporter.ShouldPublish(channel, first, now.AddSeconds(1)));
    Assert.True(reporter.ShouldPublish(channel, first with { DuplicateOrLatePackets = 3 }, now.AddSeconds(2)));
}
```

- [ ] **Step 2: Run the reporter tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~ReceiveDiagnosticsReporterTests /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because the reporter does not exist.

- [ ] **Step 3: Implement snapshot plus rate-limit reporting**

Store the last published issue counters and last publish time per channel under one lock. Return false when issue counters are unchanged, even after the time window. When counters changed inside the 500 ms window, retain the new pending snapshot and publish it on the next eligible frame rather than losing it. Remove `lastReceiveIssueUpdates` and `ShouldPublishReceiveIssue` from `MainWindowViewModel` and use the reporter.

- [ ] **Step 4: Run reporter and receive-audio tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~ReceiveDiagnosticsReporter|FullyQualifiedName~ChannelReceiveAudioCoordinator" /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 5: Commit diagnostic de-duplication**

```bash
git add src/DvmConsole.Desktop/ReceiveDiagnosticsReporter.cs src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop.Tests/ReceiveDiagnosticsReporterTests.cs
git commit -m "fix: report only new receive sequence issues"
```

### Task 4: Carry stream and source identity with decoded PCM

**Files:**
- Modify: `src/DvmConsole.Desktop/ChannelReceiveAudioCoordinator.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:4180-4200`
- Modify: `src/DvmConsole.Desktop/PatchForwardingCoordinator.cs:75-95`
- Modify: `src/DvmConsole.Desktop/CallRecordingManager.cs:230-285`
- Test: `src/DvmConsole.Desktop.Tests/ChannelReceiveAudioCoordinatorTests.cs`
- Test: `src/DvmConsole.Desktop.Tests/CallRecordingManagerTests.cs`
- Test: `src/DvmConsole.Desktop.Tests/PatchForwardingCoordinatorTests.cs`

**Interfaces:**
- Produces: receive sample callback `Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>` as `(channel, streamId, sourceId, samples)`.
- Produces: `CallRecordingManager.WriteSamples(ChannelViewModel channel, uint streamId, uint sourceId, ReadOnlyMemory<short> samples)`.
- Produces: `PatchForwardingCoordinator.ObserveDecodedSamples(ChannelViewModel source, uint streamId, uint sourceId, ReadOnlyMemory<short> samples)`.

- [ ] **Step 1: Write failing immutable-attribution tests**

In `ChannelReceiveAudioCoordinatorTests`, process a frame for stream 41, mutate the channel runtime to stream 42 before inspecting the callback, and assert the observer received 41. In `CallRecordingManagerTests`, write samples for stream 41 while the channel currently reports 42 and assert metadata records 41 and the supplied source ID.

```csharp
manager.WriteSamples(channel, streamId: 41, sourceId: 1042, new short[] { 900, -900 });
manager.ObserveTraffic(channel, Terminator(streamId: 41));
Assert.Equal((uint)41, Assert.Single(manager.LoadRecordings()).StreamId);
```

- [ ] **Step 2: Run focused attribution tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~StreamIdentity|FullyQualifiedName~ImmutableAttribution" /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because callbacks and recorder methods do not accept stream/source identity.

- [ ] **Step 3: Tag observed playback inside the per-channel process gate**

Extend `SessionState` with `uint ProcessingStreamId` and `uint ProcessingSourceId`. Before `Session.ProcessAsync`, assign both values from `traffic`. Change `ObservedAudioPlayback` to receive `Func<(uint StreamId, uint SourceId)>` and invoke the four-argument observer after the inner playback accepts the PCM. Because `ProcessGate` serializes a channel, the context cannot be overwritten by another frame on that channel.

Update `HandleDecodedSamples`:

```csharp
private void HandleDecodedSamples(
    ChannelViewModel channel,
    uint streamId,
    uint sourceId,
    ReadOnlyMemory<short> samples)
{
    patchForwarding.ObserveDecodedSamples(channel, streamId, sourceId, samples);
    callRecordings.WriteSamples(channel, streamId, sourceId, samples);
}
```

The recorder uses only supplied IDs; it must not read `channel.StreamId` or `channel.SourceId` in this method.

- [ ] **Step 4: Run audio, patch, and recording tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~ChannelReceiveAudioCoordinatorTests|FullyQualifiedName~PatchForwardingCoordinatorTests|FullyQualifiedName~CallRecordingManagerTests" /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 5: Commit stream-aware PCM routing**

```bash
git add src/DvmConsole.Desktop/ChannelReceiveAudioCoordinator.cs src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop/PatchForwardingCoordinator.cs src/DvmConsole.Desktop/CallRecordingManager.cs src/DvmConsole.Desktop.Tests/ChannelReceiveAudioCoordinatorTests.cs src/DvmConsole.Desktop.Tests/CallRecordingManagerTests.cs src/DvmConsole.Desktop.Tests/PatchForwardingCoordinatorTests.cs
git commit -m "fix: preserve receive stream identity through PCM"
```

### Task 5: Finalize and validate recordings in the background

**Files:**
- Create: `src/DvmConsole.Desktop/RecordingFinalizationQueue.cs`
- Create: `src/DvmConsole.Desktop/RecordingFinalizationResult.cs`
- Modify: `src/DvmConsole.Desktop/CallRecordingManager.cs`
- Modify: `src/DvmConsole.Desktop/CallRecordingMetadata.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs`
- Test: `src/DvmConsole.Desktop.Tests/RecordingFinalizationQueueTests.cs`
- Test: `src/DvmConsole.Desktop.Tests/CallRecordingManagerTests.cs`

**Interfaces:**
- Produces: `RecordingFinalizationQueue.EnqueueAsync(RecordingFinalizationJob job) -> ValueTask`.
- Produces: `CallRecordingManager.RecordingFinalized` event carrying `RecordingFinalizationResult`.
- Produces: `CallRecordingMetadata.PlaybackValidated` and `CallRecordingMetadata.IsPlayable`.
- Changes: `CallRecordingManager` implements `IAsyncDisposable` so shutdown drains queued finalizations.

- [ ] **Step 1: Write failing non-blocking and playable-content tests**

Use injected trimmer/encoder delegates in the finalization queue to block encoding with a `TaskCompletionSource`. Assert `ObserveTraffic` returns before the encoder is released, then await the finalized event. Add cases for `OutputSamples == 0`, a missing output file, and a decoder that yields zero samples; each must return a non-playable result without `PlaybackValidated`.

```csharp
Task observe = Task.Run(() => manager.ObserveTraffic(channel, Terminator(51)));
await observe.WaitAsync(TimeSpan.FromSeconds(1));
Assert.False(finalized.Task.IsCompleted);
encoderRelease.SetResult();
RecordingFinalizationResult result = await finalized.Task;
Assert.True(result.Metadata?.IsPlayable);
```

- [ ] **Step 2: Run finalization tests and observe synchronous blocking**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~RecordingFinalization|FullyQualifiedName~Playable" /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because encoding currently runs synchronously in `CloseCore`.

- [ ] **Step 3: Snapshot, queue, and validate finalization work**

`CloseCore` must remove the active recording under `sync`, dispose its writer, and create an immutable job containing temporary WAV path, audio format, stream/source IDs, start time, encryption, and copied channel/system/alias values. It then enqueues the job after leaving the lock.

The single-reader queue performs:

1. silence trim;
2. reject zero output samples, zero active samples, or zero peak;
3. Opus encoding to a temporary path;
4. open the encoded path through `PcmStreamDecoder` and require at least one decoded non-zero sample;
5. atomic move to the final path;
6. metadata/sidecar write with `PlaybackValidated = true`;
7. cleanup temporary inputs in `finally`;
8. publish exactly one success or failure result.

```csharp
public sealed record RecordingFinalizationResult(
    CallRecordingMetadata? Metadata,
    uint StreamId,
    string? Diagnostic,
    Exception? Error)
{
    public bool IsPlayable => Metadata?.IsPlayable == true;
}
```

`CallRecordingMetadata.IsPlayable` requires `PlaybackValidated`, positive duration/size/active samples/peak, and a nonempty path. `LoadRecordings` validates legacy sidecars once and sets the in-memory property without rewriting them.

- [ ] **Step 4: Consume completion incrementally and drain on shutdown**

Replace receive-path `RefreshRecordings()` calls with a `RecordingFinalized` handler that dispatches one result to the UI. Await `CallRecordingManager.DisposeAsync()` from `MainWindowViewModel.DisposeAsync` after receive queues stop. Failures update `AudioStatusText` once and do not insert a Play action.

- [ ] **Step 5: Run recording and playback tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~CallRecordingManagerTests|FullyQualifiedName~RecordingFinalizationQueueTests|FullyQualifiedName~RecordingPlaybackCoordinatorTests" /m:1 /p:UseSharedCompilation=false`

Expected: PASS, including an encode/decode round trip with non-zero PCM.

- [ ] **Step 6: Commit background finalization**

```bash
git add src/DvmConsole.Desktop/RecordingFinalizationQueue.cs src/DvmConsole.Desktop/RecordingFinalizationResult.cs src/DvmConsole.Desktop/CallRecordingManager.cs src/DvmConsole.Desktop/CallRecordingMetadata.cs src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop.Tests/RecordingFinalizationQueueTests.cs src/DvmConsole.Desktop.Tests/CallRecordingManagerTests.cs
git commit -m "fix: finalize playable recordings off receive workers"
```

### Task 6: Recover all selected channels after shared-route failure

**Files:**
- Modify: `src/DvmConsole.Desktop/ChannelReceiveAudioCoordinator.cs:90-150,420-480`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:4170-4190,4690-4730`
- Test: `src/DvmConsole.Desktop.Tests/ChannelReceiveAudioCoordinatorTests.cs`
- Test: `src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs`

**Interfaces:**
- Produces: `RecoverRouteAsync(ChannelViewModel failedChannel, IReadOnlyCollection<ChannelViewModel> desiredChannels, CancellationToken) -> ReceiveRouteRecoveryResult`.
- Produces: `ReconcileReceiveSessionsAsync()` in `MainWindowViewModel`.
- Consumes: `ChannelViewModel.IsAudioEnabled` as desired operator state and `ChannelReceiveAudioCoordinator.IsActive` as actual session state.

- [ ] **Step 1: Write failing route-wide recovery tests**

Start two selected channels on the same fake output route, inject a playback failure on the first write, and keep feeding traffic. Assert both logical selections remain enabled, the failed route/backend is disposed once, both sessions are rebuilt on one replacement route, and later frames from both channels reach playback. Add a separate-route test proving a failure does not restart channels bound to an unaffected device.

At the view-model level, simulate a selected channel whose coordinator session disappears while the FNE remains connected. Assert reconciliation restarts the session without toggling `IsAudioEnabled` false. Use a fake clock/trigger or direct internal method; do not add sleep-based polling to the test.

- [ ] **Step 2: Run the recovery tests and verify current per-channel behavior fails**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~RouteRecovery|FullyQualifiedName~ReceiveSessionReconciliation" /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because current recovery restarts only one channel and clears logical RX after failure.

- [ ] **Step 3: Separate desired RX state from route/session health**

Keep `ChannelViewModel.IsAudioEnabled` unchanged for recoverable audio-device/route failures. Extend the coordinator to identify every active channel on the failed normalized route, stop that route's sessions under its gate, recreate the backend/route once, and restart the affected desired channels. Return restarted and failed channel sets plus a diagnostic; never recursively call public start/stop while holding the same gate.

Keep decoder/vocoder exceptions channel-local. Only backend playback/device loss triggers route-wide recovery. If a bounded recovery attempt fails, leave desired RX enabled, expose `RX audio unavailable; retrying` once, and allow a later reconciliation trigger rather than requiring the operator to reselect every card.

- [ ] **Step 4: Add deterministic reconciliation**

Call `ReconcileReceiveSessionsAsync` after FNE transitions to Connected, after a route recovery failure, after audio-device configuration changes, and from the existing low-frequency UI/maintenance timer. It selects channels where `IsAudioEnabled && !audioCoordinator.IsActive(channel)` and starts them with a per-channel backoff guard. Cancel/drain reconciliation on shutdown and serialize it with settings-driven audio restarts.

- [ ] **Step 5: Run coordinator and view-model audio tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~ChannelReceiveAudioCoordinatorTests|FullyQualifiedName~ReceiveSessionReconciliation" /m:1 /p:UseSharedCompilation=false`

Expected: PASS, including continued PCM after replacement.

- [ ] **Step 6: Commit receive-health recovery**

```bash
git add src/DvmConsole.Desktop/ChannelReceiveAudioCoordinator.cs src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop.Tests/ChannelReceiveAudioCoordinatorTests.cs src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs
git commit -m "fix: recover shared receive audio routes"
```

### Task 7: Serialize warm-microphone desired state without cutting TX or RX

**Files:**
- Modify: `src/DvmConsole.Desktop/ChannelTransmitCoordinator.cs:70-120,360-405`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:2290-2330,4100-4160`
- Modify: `src/DvmConsole.Audio/MacCoreAudioBackend.cs`
- Test: `src/DvmConsole.Desktop.Tests/TransmitCoordinatorTests.cs`
- Test: `src/DvmConsole.Desktop.Tests/AudioRoutingViewModelTests.cs`
- Create: `src/DvmConsole.Audio.Tests/MacCoreAudioBackendTests.cs`

**Interfaces:**
- Produces: a single serialized `ReconcileKeepMicrophoneWarmAsync()` desired-state loop in `MainWindowViewModel`.
- Preserves: active transmit capture leases and active receive playback endpoints when the warm-only lease is removed.

- [ ] **Step 1: Write failing in-flight transition tests**

Enable warm capture, start a transmission, disable warm capture, emit another PCM frame, and assert the capture is still running and another traffic frame is sent. End the transmission and assert infrastructure then stops. Add rapid true/false/true view-model changes with a controllable coordinator and assert the final applied state is true, with no stale completion changing status afterward.

For the macOS voice-processing registry, open playback and capture endpoints on the same session, start both, stop/dispose capture, and assert playback remains started and usable until its own endpoint stops. Put the endpoint-count assertions behind internal test seams rather than requiring live CoreAudio hardware.

- [ ] **Step 2: Run the warm-transition tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~WarmMicrophone|FullyQualifiedName~KeepMicrophoneWarm" /m:1 /p:UseSharedCompilation=false`

Expected: the active-lease coordinator test may already pass, while the rapid desired-state test exposes the fire-and-forget race. Preserve passing safety behavior while fixing orchestration.

- [ ] **Step 3: Make lease ownership explicit**

In `SetKeepMicrophoneWarmAsync(false)`, atomically detach and dispose only `warmCaptureLease`. Recheck `active.Count` after asynchronous disposal before stopping infrastructure. `StopInfrastructureCoreAsync` must assert/guard that neither active TX leases nor a warm lease remain. Do not restart or replace the audio backend merely because the warm preference changed.

- [ ] **Step 4: Replace fire-and-forget setting application with latest-state reconciliation**

The property setter records the desired value, persists it, increments a generation, and signals one serialized reconcile loop. The loop applies the latest desired value and, if it changed while awaiting, repeats before publishing final status. Exceptions are associated with the generation that requested them; an older failure cannot overwrite newer success. Await or cancel the loop during view-model disposal.

Keep the existing warning text when warm mode is enabled. When disabled, report idle status only if no TX is active; never imply the active transmission or receive route stopped.

- [ ] **Step 5: Verify shared macOS endpoint lifetime**

Audit `VoiceProcessingSessionRegistry.RemoveEndpoint` and `StopEndpoint` so the underlying stream stops only when running endpoint count is zero and disposes only when both capture and playback endpoint counts are zero. Add the regression tests even if no production change is required.

- [ ] **Step 6: Run transmit, audio-routing, and audio-backend tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~TransmitCoordinatorTests|FullyQualifiedName~AudioRoutingViewModelTests" /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Audio.Tests/DvmConsole.Audio.Tests.csproj --no-restore --filter "FullyQualifiedName~VoiceProcessing|FullyQualifiedName~AudioBackendFactory" /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 7: Commit warm-transition safety**

```bash
git add src/DvmConsole.Desktop/ChannelTransmitCoordinator.cs src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Audio/MacCoreAudioBackend.cs src/DvmConsole.Desktop.Tests/TransmitCoordinatorTests.cs src/DvmConsole.Desktop.Tests/AudioRoutingViewModelTests.cs src/DvmConsole.Audio.Tests/MacCoreAudioBackendTests.cs
git commit -m "fix: preserve audio across warm microphone changes"
```

### Task 8: Validate concurrent traffic and recording behavior

**Files:**
- Modify: `src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs`
- Modify: `src/DvmConsole.Desktop.Tests/ChannelReceiveWorkQueueTests.cs`

**Interfaces:**
- Consumes all deliverables in Tasks 1-7.

- [ ] **Step 1: Add a busy-system regression scenario**

Drive two channels with interleaved streams, duplicates, a timeout/resume, a hard terminator, and late post-terminator voice. Assert:

```csharp
Assert.Equal(2, viewModel.CallHistory.Count(entry => !entry.IsEvent));
Assert.All(viewModel.CallHistory.Where(entry => !entry.IsEvent), entry => Assert.False(entry.IsActive));
Assert.Equal(2, finalizedRecordings.Select(item => item.StreamId).Distinct().Count());
Assert.DoesNotContain(finalizedRecordings, item => item.DurationMs == 0 || !item.IsPlayable);
Assert.Equal(1, receiveIssueMessages.Count(message => message.Contains("late/duplicate")));
```

- [ ] **Step 2: Run the busy-system regression and queue tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~BusySystem|FullyQualifiedName~ChannelReceiveWorkQueueTests" /m:1 /p:UseSharedCompilation=false`

Expected: PASS without timing-dependent sleeps; use `TaskCompletionSource` and bounded `WaitAsync` synchronization.

- [ ] **Step 3: Run Desktop and Media suites**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Media.Tests/DvmConsole.Media.Tests.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 4: Run desktop build**

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Expected: PASS with zero warnings.

- [ ] **Step 5: Commit the concurrency regression coverage**

```bash
git add src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs src/DvmConsole.Desktop.Tests/ChannelReceiveWorkQueueTests.cs
git commit -m "test: cover concurrent receive and recording stability"
```
