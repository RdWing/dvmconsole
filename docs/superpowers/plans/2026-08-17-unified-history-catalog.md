# Unified Event History Catalog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep Recorder focused on TAR configuration and make History the clean, searchable, incrementally updated catalog for events, calls, and completed recordings.

**Architecture:** Add a `HistoryCatalog` that owns stable `CallHistoryEntry` instances and indexes them by logical call and recording ID. Current-session recording completions attach to existing call rows; older recordings synthesize rows. One unified filter model feeds virtualized History views, while the compact activity sidebar observes the same stable entries without catalog-wide rebuilds.

**Tech Stack:** .NET 10, C#, Avalonia 11.3.18, observable collections, background catalog scan, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-17-channel-history-stream-stability-design.md`

## Global Constraints

- Recorder contains storage, retention, TAR setup, per-channel enablement, and ignored RID configuration only.
- Completed recordings and all recording catalog/search controls live in History.
- A current-session call with a recording is one row, not a call row plus a recording row.
- Older recordings appear in History even when their live event was never in memory.
- Catalog updates are incremental; do not clear/repopulate the entire collection on call or recording completion.
- Play is visible only for finalized, validated audio.
- Incoming traffic cannot replace/recreate the active playback row or block playback.
- Busy lists use a virtualizing items control, not `ItemsControl` nested in an outer `ScrollViewer`.

---

### Task 1: Build a stable indexed History catalog

**Files:**
- Create: `src/DvmConsole.Desktop/HistoryCatalog.cs`
- Modify: `src/DvmConsole.Desktop/CallHistory.cs`
- Create: `src/DvmConsole.Desktop.Tests/HistoryCatalogTests.cs`
- Modify: `src/DvmConsole.Desktop.Tests/CallHistoryStoreTests.cs`

**Interfaces:**
- Produces: `HistoryCallKey(system, protocol, direction, streamId, startBucket)` and stable recording ID keys.
- Produces: `HistoryCatalog.AddCall`, `CompleteCall`, `AddEvent`, `AttachRecording`, `RemoveRecording`, and `ClearSessionHistory`.
- Exposes: newest-first `ReadOnlyObservableCollection<CallHistoryEntry> Entries`.

- [ ] **Step 1: Write failing merge and synthesis tests**

Cover:

1. adding an RX call then attaching a matching finalized recording updates the same object and keeps collection count one;
2. attaching an older recording creates a completed recording-only row with duration, aliases, direction, protocol, system, encryption, and diagnostics;
3. loading that recording before the matching live call and then adding the call de-duplicates to one row;
4. equal stream IDs on different systems/protocols/directions do not collide;
5. removing a current-session recording keeps its call row and clears Play;
6. removing a recording-only row removes the row;
7. repeated completion notifications are idempotent.

Use the recording's stable relative path or metadata ID as the recording key. Use exact system/protocol/direction/stream plus bounded start-time proximity for matching; do not match solely by filename or stream ID.

- [ ] **Step 2: Run the catalog tests and verify missing behavior**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~HistoryCatalogTests|FullyQualifiedName~CallHistoryTests" /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because older recordings cannot synthesize rows and refresh currently rebuilds associations.

- [ ] **Step 3: Extend the entry model for unified rows**

Add immutable identity plus `IsRecordingOnly`, `DirectionText`, recording filename/path/size/diagnostic projections, and expandable-detail state as needed. `SetRecording` raises every dependent property (`HasRecording`, duration when sourced from metadata, details, size, filename, playable state) without replacing the entry instance.

Keep live event/call factory methods. Add a recording-only factory that maps all `CallRecordingMetadata` fields and starts completed. Do not make operational events pretend to have a DMR protocol.

- [ ] **Step 4: Implement indexed incremental mutation**

Maintain dictionaries from active/logical call keys and recording IDs to entries. Insert/remove/move only affected rows on the UI dispatcher. The catalog does not scan `Entries` for every packet. Retain a bounded live-session history separately from disk-backed recording-only rows so the existing 100-event cap cannot hide the recording archive.

- [ ] **Step 5: Run catalog tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~HistoryCatalogTests|FullyQualifiedName~CallHistoryTests" /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 6: Commit the catalog model**

```bash
git add src/DvmConsole.Desktop/HistoryCatalog.cs src/DvmConsole.Desktop/CallHistory.cs src/DvmConsole.Desktop.Tests/HistoryCatalogTests.cs src/DvmConsole.Desktop.Tests/CallHistoryStoreTests.cs
git commit -m "feat: add unified history catalog"
```

### Task 2: Load and update recording metadata without UI churn

**Files:**
- Create: `src/DvmConsole.Desktop/RecordingCatalogLoader.cs`
- Modify: `src/DvmConsole.Desktop/CallRecordingManager.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:1220-1545,3030-3110,3920-3935,4350-4405`
- Create: `src/DvmConsole.Desktop.Tests/RecordingCatalogLoaderTests.cs`
- Modify: `src/DvmConsole.Desktop.Tests/CallRecordingManagerTests.cs`
- Modify: `src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs`

**Interfaces:**
- Consumes: `RecordingFinalizationResult` from the receive/recording stability plan.
- Produces: background `ScanAsync(root, cancellationToken)` batches and per-recording add/update/remove notifications.
- Produces: one stable `HistoryCatalog` in `MainWindowViewModel`.

- [ ] **Step 1: Write failing incremental-update tests**

Seed 1,000 sidecars, perform the initial scan off the calling synchronization context, and publish bounded batches. Record references to several `CallHistoryEntry` objects, finalize one new recording, and assert existing objects remain reference-equal and the collection never emits Reset. Delete one item and assert only its attachment/row changes.

Add a busy simulation where 100 live calls and 100 recording completions arrive while a fake playback coordinator is active. Assert the playback start count remains one and no catalog operation calls Stop.

- [ ] **Step 2: Run the tests and verify `RefreshRecordingsCore` resets the collection**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~RecordingCatalogLoader|FullyQualifiedName~IncrementalHistoryCatalog" /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because `recordingEntries.Clear()` and full `LoadRecordings()` scans occur on the UI path.

- [ ] **Step 3: Implement background discovery and bounded dispatch**

Enumerate/read sidecars and validate legacy recordings off the UI thread. Sort immutable results and dispatch small batches to `HistoryCatalog.AttachRecording`. Cancel an older scan when the root changes or the view model disposes. Manual refresh starts a new scan; routine finalization does not.

Replace all receive/terminator/TX `RefreshRecordings()` calls with the specific finalized result from `CallRecordingManager`. Retention pruning and delete publish exact removed IDs. Keep an explicit full rescan only for startup, root change, and manual recovery.

- [ ] **Step 4: Route call lifecycle into the catalog**

Replace direct `CallHistoryStore` mutation and `FindRecordingForHistoryEntry` scans with catalog operations using the immutable stream/source identity from the receive stability plan. `CallHistory`, `ActivityCallHistory`, and external History windows reference catalog entries; compatibility properties may remain temporarily but cannot own a second collection.

- [ ] **Step 5: Run incremental and busy tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~RecordingCatalogLoader|FullyQualifiedName~HistoryCatalog|FullyQualifiedName~BusyCatalog" /m:1 /p:UseSharedCompilation=false`

Expected: PASS without collection Reset notifications or playback restarts.

- [ ] **Step 6: Commit incremental catalog integration**

```bash
git add src/DvmConsole.Desktop/RecordingCatalogLoader.cs src/DvmConsole.Desktop/CallRecordingManager.cs src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop.Tests/RecordingCatalogLoaderTests.cs src/DvmConsole.Desktop.Tests/CallRecordingManagerTests.cs src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs
git commit -m "fix: update history catalog incrementally"
```

### Task 3: Consolidate all search and metadata into History

**Files:**
- Create: `src/DvmConsole.Desktop/HistoryCatalogFilter.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:2580-2945`
- Modify: `src/DvmConsole.Core/Settings/UserSettings.cs`
- Create: `src/DvmConsole.Desktop.Tests/HistoryCatalogFilterTests.cs`
- Modify: `src/DvmConsole.Core.Tests/UserSettingsStoreTests.cs`

**Interfaces:**
- Produces one primary query and advanced direction/protocol/encryption/system/channel/talkgroup/subscriber/alias/date filters.
- Produces: `HistoryFilterSummary`, `HasAdvancedHistoryFilters`, and `ClearHistoryFilters()`.
- Consumes both call/event and attached recording metadata.

- [ ] **Step 1: Write failing unified-filter tests**

Build mixed operational events, RX/TX calls, current recordings, and recording-only entries. Assert the primary query matches system, channel, RID, alias, TGID, protocol, encryption, event message, filename, and technical diagnostics. Assert every advanced filter combines with AND semantics and inclusive local start/end dates. Events without a requested recording field must fail that field's filter predictably.

Assert the filter summary is empty at defaults, concise with active filters, and Clear resets all fields in one notification batch.

- [ ] **Step 2: Run the filter tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~HistoryCatalogFilterTests /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because History search and recording filters are separate.

- [ ] **Step 3: Implement one filter object and migrate settings**

Move the useful `RecordingCatalogFilter` matching rules into `HistoryCatalogFilter`, extended for event/call fields. Expose one filter instance or flattened properties from the view model. Preserve compatible saved column/filter preferences where useful, but remove Recorder-only filter state after migration. Coalesce property notifications so typing one search character does not repeatedly enumerate and notify unrelated collections.

- [ ] **Step 4: Run filter and settings tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~HistoryCatalogFilterTests|FullyQualifiedName~UserSettingsStoreTests" /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 5: Commit unified filtering**

```bash
git add src/DvmConsole.Desktop/HistoryCatalogFilter.cs src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Core/Settings/UserSettings.cs src/DvmConsole.Desktop.Tests/HistoryCatalogFilterTests.cs src/DvmConsole.Core.Tests/UserSettingsStoreTests.cs
git commit -m "feat: unify history and recording filters"
```

### Task 4: Reorganize Recorder and build a clean virtualized History surface

**Files:**
- Modify: `src/DvmConsole.Desktop/OperatorToolsWindow.axaml:190-330`
- Modify: `src/DvmConsole.Desktop/OperatorToolsWindow.axaml.cs:220-335`
- Modify: `src/DvmConsole.Desktop/CallHistoryWindow.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml:255-280`
- Create: `src/DvmConsole.Desktop/HistoryEntryActions.cs`
- Create: `src/DvmConsole.Desktop.Tests/HistoryEntryActionsTests.cs`

**Interfaces:**
- Consumes: stable filtered History entries from Tasks 1-3.
- Produces: compact row plus expandable recording details and serialized Play/Stop/Open/Delete actions.

- [ ] **Step 1: Add action-state tests**

Assert Play appears only for `Recording.IsPlayable`, Stop reflects the one active playback path, Open/Delete are unavailable for missing/invalid files, and Delete has different postconditions for live-call versus recording-only rows. Assert an incoming catalog entry does not mutate the active row's playback/action state.

- [ ] **Step 2: Make Recorder configuration-only**

Remove search, filters, field selection, completed recordings, and Play/Stop/Open/Delete from Recorder. Keep storage and retention at the top. Group channel TAR enablement and ignored-RID editing in one collapsed `Expander` per system; show a compact enabled-count summary in each header. Keep TAR setup adjacent to those channel controls.

- [ ] **Step 3: Build the History header and compact row**

Use a primary search field beside Export/Clear. Put advanced filters in a collapsed `Expander`; when filters are active, show `HistoryFilterSummary` and one Clear filters action even while collapsed.

The default row shows date/time, channel/system, caller alias/RID to TGID, direction/protocol, encryption, duration, and Play when validated. Put filename, format, byte size, audio diagnostics, Open, Stop, and Delete inside row details/overflow. Keep rows narrow enough for the existing tools window.

- [ ] **Step 4: Virtualize full History and stabilize the sidebar**

Replace outer-ScrollViewer plus ItemsControl combinations with `ListBox`/`ItemsRepeater` using a virtualizing panel and its own scroll viewer in both Operator Tools and the standalone History window. The activity sidebar remains compact and bounded to recent entries; use a stable play command/state binding instead of click handlers that depend on regenerated button instances.

Do not recreate data templates or replace `ItemsSource` when catalog content changes. Preserve keyboard focus and the expanded row while new calls arrive.

- [ ] **Step 5: Wire safe actions**

Serialize Play/Stop through the existing playback coordinator. Validate the path again immediately before Play/Open/Delete. Confirm Delete, remove audio plus sidecar, then invoke `HistoryCatalog.RemoveRecording`; retain a live call row and remove a recording-only row as specified.

- [ ] **Step 6: Build and run action/catalog tests**

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~HistoryEntryActions|FullyQualifiedName~HistoryCatalog|FullyQualifiedName~HistoryCatalogFilter" /m:1 /p:UseSharedCompilation=false`

Expected: PASS with compiled bindings.

- [ ] **Step 7: Commit the Recorder/History reorganization**

```bash
git add src/DvmConsole.Desktop/OperatorToolsWindow.axaml src/DvmConsole.Desktop/OperatorToolsWindow.axaml.cs src/DvmConsole.Desktop/CallHistoryWindow.cs src/DvmConsole.Desktop/MainWindow.axaml src/DvmConsole.Desktop/HistoryEntryActions.cs src/DvmConsole.Desktop.Tests/HistoryEntryActionsTests.cs
git commit -m "feat: consolidate recordings into history"
```

### Task 5: Validate busy-system catalog and playback behavior

**Files:**
- Modify: `src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs`
- Modify: `src/DvmConsole.Desktop.Tests/RecordingPlaybackCoordinatorTests.cs`

**Interfaces:**
- Consumes all deliverables in Tasks 1-4 and finalized recordings from the receive/recording plan.

- [ ] **Step 1: Add a deterministic busy-system scenario**

While a ten-second synthetic recording plays, add interleaved calls on multiple systems, complete recordings, apply/remove filters, and append sidebar entries. Assert PCM playback remains continuous in order, the coordinator starts once, Play state remains attached to the same entry, catalog collection changes are Add/Remove/Move/Replace rather than Reset, and no duplicate logical calls appear.

Include a timeout/resume call and a hard-terminated late packet from the receive-lifecycle plan. Assert the former has one row/recording and the latter cannot create another.

- [ ] **Step 2: Run focused busy tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~BusyHistory|FullyQualifiedName~RecordingPlaybackCoordinatorTests" /m:1 /p:UseSharedCompilation=false`

Expected: PASS without timing sleeps; synchronize with test signals and bounded waits.

- [ ] **Step 3: Run complete Desktop and Core suites**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Core.Tests/DvmConsole.Core.Tests.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 4: Perform operator validation**

On a busy replay/test system, play multiple old and newly finalized recordings while calls continue. Confirm audio remains smooth, sidebar Play has no graphical race, silent/invalid files have no Play action, and no call splits after timeout continuation. Verify Recorder opens compactly with collapsed systems and History contains all former completed-recording details and filters without looking crowded.

- [ ] **Step 5: Commit final regression coverage**

```bash
git add src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs src/DvmConsole.Desktop.Tests/RecordingPlaybackCoordinatorTests.cs
git commit -m "test: validate busy unified history playback"
```
