# Dialog and Recent Codeplug Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make transient dialogs fit their content without excessive height and make long Open Recent codeplug paths readable without changing the path that opens.

**Architecture:** Replace four ad hoc message/prompt window constructions with one bounded dialog factory. Introduce a pure recent-path presentation helper and a menu-builder overload that renders bounded two-line headers while retaining the exact path in `MenuItem.Tag`.

**Tech Stack:** .NET 10, C#, Avalonia 11.3.18, compiled AXAML-compatible controls, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-17-channel-history-stream-stability-design.md`

## Global Constraints

- Short dialogs size to content; long messages scroll inside a bounded window.
- The Unable to open codeplug dialog must not have a large fixed minimum height.
- Confirmation, information, text prompt, and codeplug-error dialogs share sizing behavior.
- Recent-codeplug display may elide, but the click payload must remain the exact original path.
- Full recent paths must be available in a tooltip and accessible name.
- Do not alter recent-codeplug ordering, persistence, or maximum-count behavior.

---

### Task 1: Define and test bounded dialog construction

**Files:**
- Create: `src/DvmConsole.Desktop/OperatorDialogFactory.cs`
- Create: `src/DvmConsole.Desktop.Tests/OperatorDialogFactoryTests.cs`

**Interfaces:**
- Produces: `CreateMessage(title, message, closeLabel) -> OperatorDialogParts`.
- Produces: `CreateConfirmation(title, message, confirmLabel) -> OperatorDialogParts`.
- Produces: `CreateTextPrompt(title, message, confirmLabel, watermark) -> OperatorDialogParts`.
- Exposes controls needed by the shell to attach close/result handlers without searching visual trees.

- [ ] **Step 1: Write failing layout-contract tests**

Create each dialog and assert:

```csharp
Assert.Equal(SizeToContent.Height, parts.Window.SizeToContent);
Assert.InRange(parts.Window.Width, 420, 720);
Assert.True(parts.Window.MaxHeight > 0);
Assert.True(parts.MessageScroller.MaxHeight > 0);
Assert.Equal(TextWrapping.Wrap, parts.MessageText.TextWrapping);
Assert.False(parts.Window.CanResize);
```

Assert no variant sets `MinHeight` to 210/220/240. Verify the prompt exposes its text box and that action buttons have stable minimum widths.

- [ ] **Step 2: Run the tests and verify the factory is missing**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~OperatorDialogFactoryTests /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because `OperatorDialogFactory` does not exist.

- [ ] **Step 3: Implement a content-sized, bounded factory**

Build dialog content from a `Grid` with `Auto,*,Auto` rows. Put the wrapped message in a `ScrollViewer` with a practical maximum height, vertical scrolling automatic, and horizontal scrolling disabled. Set a stable width, `SizeToContent=Height`, a maximum height, no excessive `MinHeight`, and `WindowStartupLocation.CenterOwner`.

Return a small parts record containing the window, message scroller, input when present, cancel button when present, and primary button. Keep result semantics in `MainWindow`; the factory owns presentation only.

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~OperatorDialogFactoryTests /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 5: Commit the shared dialog layout**

```bash
git add src/DvmConsole.Desktop/OperatorDialogFactory.cs src/DvmConsole.Desktop.Tests/OperatorDialogFactoryTests.cs
git commit -m "feat: add bounded operator dialogs"
```

### Task 2: Migrate every ad hoc main-window dialog

**Files:**
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:550-680,825-860`
- Modify: `src/DvmConsole.Desktop.Tests/OperatorDialogFactoryTests.cs`

**Interfaces:**
- Consumes: `OperatorDialogFactory` from Task 1.
- Preserves: existing async return values and owner-centered modal behavior.

- [ ] **Step 1: Replace the four duplicated constructions**

Migrate `ConfirmAsync`, `PromptForTextAsync`, `ShowCodeplugErrorAsync`, and `ShowInformationAsync`. Attach the existing confirm/cancel/close logic to returned buttons, preserve input focus on open, and show each dialog with `this` as owner.

Search `src/DvmConsole.Desktop` for `new Window` and document every remaining case in a test comment or short code comment. Modeless top-level tools and dedicated `Window` subclasses are not migrated merely because they have fixed minimum sizes; this task targets transient message/input dialogs with duplicated layout.

- [ ] **Step 2: Add the specific codeplug-error regression**

Add a named test `UnableToOpenCodeplugUsesCompactBoundedMessageDialog` asserting the same factory variant used by `ShowCodeplugErrorAsync` has content height sizing, no tall minimum, and internal scrolling for long diagnostics.

- [ ] **Step 3: Build and run dialog tests**

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~OperatorDialogFactoryTests /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 4: Commit the dialog migration**

```bash
git add src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop.Tests/OperatorDialogFactoryTests.cs
git commit -m "fix: size transient dialogs to content"
```

### Task 3: Present long recent-codeplug paths without clipping identity

**Files:**
- Create: `src/DvmConsole.Desktop/RecentCodeplugPresentation.cs`
- Modify: `src/DvmConsole.Desktop/MainWindowMenuBuilder.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:300-315`
- Create: `src/DvmConsole.Desktop.Tests/RecentCodeplugPresentationTests.cs`
- Create: `src/DvmConsole.Desktop.Tests/MainWindowMenuBuilderTests.cs`

**Interfaces:**
- Produces: `RecentCodeplugPresentation.FromPath(string path, int parentBudget) -> RecentCodeplugPresentation`.
- Produces: `MainWindowMenuBuilder.ReplaceRecentCodeplugItems(...)`.
- Preserves: `MenuItem.Tag == fullOriginalPath`.

- [ ] **Step 1: Write failing path-presentation tests**

Cover a short path, a very long Unix path, a Windows-style path, duplicate filenames under different parents, a root-level path, and Unicode names. Assert the filename remains complete, the parent text stays within its character budget using a middle ellipsis, and different parent paths remain distinguishable near the leaf when possible. The helper must never use `File.Exists` or require the path to be valid on the current OS.

- [ ] **Step 2: Write failing menu-item preservation tests**

Build a menu with a 300-character path and assert:

- the item header is a bounded control, not the raw string;
- the primary line is the filename;
- the secondary line is the compact parent;
- `ToolTip.GetTip(item)` and `AutomationProperties.GetName(item)` contain the full path;
- `item.Tag` is the exact same string supplied;
- invoking the item still sends that exact path to the click handler.

- [ ] **Step 3: Run the focused tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~RecentCodeplugPresentation|FullyQualifiedName~MainWindowMenuBuilder" /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because path-aware presentation is absent.

- [ ] **Step 4: Implement bounded two-line recent entries**

`RecentCodeplugPresentation` separates the final filename from the parent using both directory separator styles. It elides the middle of an over-budget parent while preserving its root/prefix and the closest parent segment. Treat empty/malformed values defensively and retain the original string as `FullPath`.

Create each menu header as a vertical panel with a prominent filename and a smaller muted parent line. Bound the content width (approximately 480-560 device-independent pixels), apply end ellipsis as a final renderer safeguard, and attach the full path as tooltip and accessible name. Keep named-settings profile menus on the existing plain-string overload.

- [ ] **Step 5: Route Open Recent through the specialized builder**

Change only `RefreshRecentCodeplugMenu` to call `ReplaceRecentCodeplugItems`. `HandleOpenRecentCodeplugClick` continues to read the full path from `Tag`; do not reconstruct it from the display text.

- [ ] **Step 6: Run tests and compile AXAML**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~RecentCodeplugPresentation|FullyQualifiedName~MainWindowMenuBuilder" /m:1 /p:UseSharedCompilation=false`

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 7: Commit recent-path layout**

```bash
git add src/DvmConsole.Desktop/RecentCodeplugPresentation.cs src/DvmConsole.Desktop/MainWindowMenuBuilder.cs src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop.Tests/RecentCodeplugPresentationTests.cs src/DvmConsole.Desktop.Tests/MainWindowMenuBuilderTests.cs
git commit -m "fix: present long recent codeplug paths"
```

### Task 4: Validate desktop layout behavior

**Files:**
- No additional production files.

**Interfaces:**
- Consumes all deliverables in Tasks 1-3.

- [ ] **Step 1: Run the complete desktop suite and build**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 2: Perform macOS and cross-platform layout validation**

Open a malformed codeplug with both short and multi-paragraph diagnostics. Confirm the window is compact for short text and internally scrollable for long text. Exercise confirmation, information, and profile-name prompts to confirm consistent sizing and focus.

Populate Open Recent with paths longer than the main-window width and with duplicate filenames from different parents. Confirm the menu remains on screen, entries are distinguishable, full paths appear on hover, keyboard navigation works, and each item opens the correct path.

- [ ] **Step 3: Commit any test-only corrections**

```bash
git add src/DvmConsole.Desktop.Tests
git commit -m "test: validate dialog and recent path layout"
```
