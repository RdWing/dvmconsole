# Channel Controls and Application Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make channel-card controls reliable and visually stable, add global/zone receive actions, distinguish system status dots, show receive activity on system and zone tabs, and show `DVM Console` in the macOS application menu.

**Architecture:** Isolate card hit testing and system accent selection in small testable helpers. Keep button appearance entirely binding/template driven, and route all bulk receive actions through the same start/stop methods used by individual cards. Set the Avalonia application identity explicitly while retaining the already-correct macOS bundle metadata.

**Tech Stack:** .NET 10, C#, Avalonia 11.3.18, compiled AXAML bindings, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-17-channel-history-stream-stability-design.md`

## Global Constraints

- BER reporting is out of scope.
- Menu labels must say `(zone)`, never `(tab)`.
- Only non-interactive channel-card space may toggle receive or begin a drag.
- Enabled TX/PAGE/ALERT/TAR colors must remain visible during hover and press.
- System accent assignment must be deterministic across launches.
- Receive activity must be visible on unselected system and zone tabs without conflating activity, selection, and connection state.
- The macOS application-menu name must be `DVM Console` for bundled and unbundled launches.
- Keyboard global PTT must be optional and use one portable Space/F1-F19 mapping for focused and OS-global input.
- Do not add a new runtime or NuGet dependency.

---

### Task 1: Reject nested interactive pointer sources

**Files:**
- Create: `src/DvmConsole.Desktop/ChannelCardInput.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:110-190`
- Test: `src/DvmConsole.Desktop.Tests/ChannelCardInputTests.cs`

**Interfaces:**
- Produces: `ChannelCardInput.IsInteractiveSource(object? source, Control card) -> bool`.
- Consumes: Avalonia logical and visual parent relationships.

- [ ] **Step 1: Write the failing nested-content tests**

```csharp
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ChannelCardInputTests
{
    [Fact]
    public void NestedButtonContentIsInteractive()
    {
        var text = new TextBlock { Text = "TAR" };
        var button = new Button { Content = text };
        var card = new Border { Child = button };

        Assert.True(ChannelCardInput.IsInteractiveSource(text, card));
        Assert.True(ChannelCardInput.IsInteractiveSource(button, card));
    }

    [Fact]
    public void SliderAndItsTemplateDescendantsAreInteractive()
    {
        var thumb = new Thumb();
        var slider = new Slider { Tag = thumb };
        var card = new Border { Child = slider };

        Assert.True(ChannelCardInput.IsInteractiveSource(slider, card));
    }

    [Fact]
    public void PlainCardContentIsNotInteractive()
    {
        var label = new TextBlock { Text = "Dispatch" };
        var card = new Border { Child = label };

        Assert.False(ChannelCardInput.IsInteractiveSource(label, card));
    }
}
```

- [ ] **Step 2: Run the focused test and verify the helper is missing**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~ChannelCardInputTests /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because `ChannelCardInput` does not exist.

- [ ] **Step 3: Implement ancestry-aware hit testing**

```csharp
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace DvmConsole.Desktop;

internal static class ChannelCardInput
{
    public static bool IsInteractiveSource(object? source, Control card)
    {
        ArgumentNullException.ThrowIfNull(card);
        object? current = source;
        while (current is not null && !ReferenceEquals(current, card))
        {
            if (current is Button or Slider)
                return true;

            current = current is Avalonia.Visual visual
                ? visual.GetVisualParent() ?? (current as ILogical)?.LogicalParent
                : (current as ILogical)?.LogicalParent;
        }

        return false;
    }
}
```

In `HandleChannelPointerPressed`, replace `e.Source is Button or Slider` with:

```csharp
if (sender is not Control card || ChannelCardInput.IsInteractiveSource(e.Source, card))
    return;
```

Keep the existing movement threshold, capture, release, and persistence behavior unchanged.

- [ ] **Step 4: Run hit-testing and channel view-model tests**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~ChannelCardInputTests|FullyQualifiedName~ChannelViewModelTests" /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 5: Commit the input fix**

```bash
git add src/DvmConsole.Desktop/ChannelCardInput.cs src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop.Tests/ChannelCardInputTests.cs
git commit -m "fix: isolate channel card pointer gestures"
```

### Task 2: Give channel action buttons one visual surface

**Files:**
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml:14-46,232-238`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:850-935`
- Test: `src/DvmConsole.Desktop.Tests/ChannelViewModelTests.cs`

**Interfaces:**
- Consumes: `TransmitSelectionBrush`, `PageSelectionBrush`, `AlertSelectionBrush`, and `RecordingSelectionBrush` from `ChannelViewModel`.
- Produces: a `channel-action` button template whose border uses only `TemplateBinding` values.

- [ ] **Step 1: Extend the state-notification tests**

Add a test that subscribes to `PropertyChanged`, toggles all four selections, and asserts the brush properties changed immediately without a second state transition:

```csharp
[Fact]
public void ActionSelectionBrushesNotifyImmediately()
{
    ChannelViewModel channel = CreateTransmitCapableChannel();
    var changed = new List<string?>();
    channel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

    channel.SetTransmitSelected(true);
    channel.SetPageSelected(true);
    channel.SetAlertSelected(true);
    channel.SetRecordingEnabled(true);

    Assert.Contains(nameof(ChannelViewModel.TransmitSelectionBrush), changed);
    Assert.Contains(nameof(ChannelViewModel.PageSelectionBrush), changed);
    Assert.Contains(nameof(ChannelViewModel.AlertSelectionBrush), changed);
    Assert.Contains(nameof(ChannelViewModel.RecordingSelectionBrush), changed);
    Assert.NotEqual(Color.Parse("#E8EDF3"), Assert.IsType<SolidColorBrush>(channel.RecordingSelectionBrush).Color);
}
```

Use the existing channel factory pattern in this test file rather than introducing a second configuration fixture.

- [ ] **Step 2: Run the focused state test**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~ActionSelectionBrushesNotifyImmediately /m:1 /p:UseSharedCompilation=false`

Expected: PASS for model notification; the UI defect remains reproducible manually.

- [ ] **Step 3: Replace competing hover styles and local brush writes**

Create one template for `Button.channel-action`:

```xml
<Style Selector="Button.channel-action">
  <Setter Property="Template">
    <ControlTemplate>
      <Border Background="{TemplateBinding Background}"
              BorderBrush="{TemplateBinding BorderBrush}"
              BorderThickness="{TemplateBinding BorderThickness}"
              CornerRadius="3"
              Padding="{TemplateBinding Padding}">
        <ContentPresenter Content="{TemplateBinding Content}"
                          HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}"
                          VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}" />
      </Border>
    </ControlTemplate>
  </Setter>
</Style>
<Style Selector="Button.channel-action:disabled">
  <Setter Property="Opacity" Value="0.55" />
</Style>
```

Add `channel-action` to TX, PAGE, ALERT, TAR, and encryption buttons. Remove the duplicated `:pointerover`/`:pressed` rules for both the `Button` and `PART_ContentPresenter`. Remove `PointerEntered` handlers and delete `Handle*PointerEntered`/`Apply*ButtonBrush`. Click handlers should only toggle view-model state:

```csharp
private void HandleTransmitSelectionClick(object? sender, RoutedEventArgs e)
{
    if (sender is Button { DataContext: ChannelViewModel channel })
        viewModel.ToggleChannelTransmitSelection(channel);
}
```

Apply the same shape to PAGE and ALERT. TAR continues using `RecordingCommand`.

- [ ] **Step 4: Build compiled AXAML and rerun state tests**

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~ChannelViewModelTests /m:1 /p:UseSharedCompilation=false`

Expected: both PASS with no AXAML binding or template errors.

- [ ] **Step 5: Commit the visual-state fix**

```bash
git add src/DvmConsole.Desktop/MainWindow.axaml src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop.Tests/ChannelViewModelTests.cs
git commit -m "fix: stabilize channel action button states"
```

### Task 3: Add global and selected-zone receive actions

**Files:**
- Create: `src/DvmConsole.Desktop/ReceiveSelectionScope.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml:78-84`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:675-690,3380-3410`
- Test: `src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs`

**Interfaces:**
- Produces: `EnableAllReceiveAsync()`, `EnableSelectedZoneReceiveAsync()`, `DisableSelectedZoneReceiveAsync()`.
- Produces: `GetReceiveScopeChannels(ReceiveSelectionScope scope) -> IReadOnlyList<ChannelViewModel>` for deterministic scope testing.

- [ ] **Step 1: Write failing scope-selection tests**

Using `TestData/multiple-systems.yml`, assert:

```csharp
[Fact]
public async Task ReceiveScopesDistinguishAllSystemsFromSelectedZone()
{
    await using MainWindowViewModel viewModel = await LoadMultipleSystemsAsync();
    viewModel.SelectedSystem = viewModel.Systems[0];
    viewModel.Systems[0].SelectedZone = viewModel.Systems[0].Zones[1];

    Assert.Equal(5, viewModel.GetReceiveScopeChannels(ReceiveSelectionScope.All).Count);
    Assert.Equal(
        viewModel.Systems[0].Zones[1].Channels,
        viewModel.GetReceiveScopeChannels(ReceiveSelectionScope.SelectedZone));
}
```

- [ ] **Step 2: Run the scope test and verify missing symbols**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~ReceiveScopesDistinguishAllSystemsFromSelectedZone /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because `ReceiveSelectionScope` and `GetReceiveScopeChannels` do not exist.

- [ ] **Step 3: Implement receive scopes and menu handlers**

```csharp
namespace DvmConsole.Desktop;

internal enum ReceiveSelectionScope
{
    All,
    SelectedZone
}
```

```csharp
internal IReadOnlyList<ChannelViewModel> GetReceiveScopeChannels(ReceiveSelectionScope scope)
    => scope switch
    {
        ReceiveSelectionScope.All => Systems.SelectMany(system => system.Channels).Distinct().ToArray(),
        ReceiveSelectionScope.SelectedZone => SelectedSystem?.SelectedZone?.Channels.Distinct().ToArray() ?? [],
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

private async Task SetReceiveAsync(ReceiveSelectionScope scope, bool enabled)
{
    foreach (ChannelViewModel channel in GetReceiveScopeChannels(scope))
    {
        if (enabled && !channel.IsAudioEnabled)
            await StartAudioAsync(channel).ConfigureAwait(false);
        else if (!enabled && channel.IsAudioEnabled)
            await StopAudioAsync(channel).ConfigureAwait(false);
    }
}

public Task EnableAllReceiveAsync() => SetReceiveAsync(ReceiveSelectionScope.All, true);
public Task EnableSelectedZoneReceiveAsync() => SetReceiveAsync(ReceiveSelectionScope.SelectedZone, true);
public Task DisableSelectedZoneReceiveAsync() => SetReceiveAsync(ReceiveSelectionScope.SelectedZone, false);
```

Add menu headers exactly:

```xml
<MenuItem Header="Enable all receive" Click="HandleEnableAllReceiveClick" />
<MenuItem Header="Enable all receive (zone)" Click="HandleEnableZoneReceiveClick" />
<MenuItem Header="Disable all receive (zone)" Click="HandleDisableZoneReceiveClick" />
<MenuItem Header="Disable all receive" Click="HandleDisableAllReceiveClick" />
```

- [ ] **Step 4: Run scope tests and build AXAML**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~ReceiveScopes|FullyQualifiedName~LoadsVariableSystemTabs" /m:1 /p:UseSharedCompilation=false`

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 5: Commit the receive actions**

```bash
git add src/DvmConsole.Desktop/ReceiveSelectionScope.cs src/DvmConsole.Desktop/MainWindow.axaml src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs
git commit -m "feat: add global and zone receive actions"
```

### Task 4: Assign distinct system accents and system/zone receive bars

**Files:**
- Create: `src/DvmConsole.Desktop/SystemAccentPalette.cs`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml:202-208`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:3560-3585,6490-6585`
- Test: `src/DvmConsole.Desktop.Tests/SystemAccentPaletteTests.cs`
- Test: `src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs`
- Create: `src/DvmConsole.Desktop.Tests/ZoneViewModelTests.cs`

**Interfaces:**
- Produces: `SystemAccentPalette.GetBrush(int systemIndex) -> IBrush`.
- Produces: `SystemViewModel.StatusGlyph` and `SystemViewModel.StatusAccentBrush`.
- Produces: `SystemViewModel.IsReceiving` and `ZoneViewModel.IsReceiving`, derived from channel runtime state.
- Produces: activity-bar brush/opacity properties suitable for compiled AXAML binding.

- [ ] **Step 1: Write failing palette and glyph tests**

```csharp
[Fact]
public void AdjacentSystemsUseStableDistinctAccents()
{
    Color first = Assert.IsType<SolidColorBrush>(SystemAccentPalette.GetBrush(0)).Color;
    Color second = Assert.IsType<SolidColorBrush>(SystemAccentPalette.GetBrush(1)).Color;

    Assert.NotEqual(first, second);
    Assert.Equal(first, Assert.IsType<SolidColorBrush>(SystemAccentPalette.GetBrush(0)).Color);
}
```

Extend the multiple-system load test to assert Alpha and Beta have different `StatusAccentBrush` values and disconnected systems expose `○`.

Add system and zone aggregation tests that place a channel into `ChannelRuntimeState.Receiving`, assert only its owning system and zone report activity, then terminate the call and assert both clear. Include two simultaneously active zones and verify both remain active even when neither tab is selected.

- [ ] **Step 2: Run the tests and verify missing palette members**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~SystemAccentPalette|FullyQualifiedName~LoadsVariableSystemTabs" /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because the palette and properties do not exist.

- [ ] **Step 3: Implement palette assignment and split tab content**

```csharp
internal static class SystemAccentPalette
{
    private static readonly Color[] Colors =
    [
        Color.Parse("#38BDF8"), Color.Parse("#F97316"), Color.Parse("#A78BFA"),
        Color.Parse("#22C55E"), Color.Parse("#F43F5E"), Color.Parse("#EAB308"),
        Color.Parse("#14B8A6"), Color.Parse("#EC4899")
    ];

    public static IBrush GetBrush(int systemIndex)
    {
        if (systemIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(systemIndex));
        return new SolidColorBrush(Colors[systemIndex % Colors.Length]);
    }
}
```

Pass the configuration index into `SystemViewModel`, store the brush once, and expose:

```csharp
public IBrush StatusAccentBrush { get; }
public string StatusGlyph => IsConnected ? "●" : "○";
public bool IsReceiving => Channels.Any(channel => channel.State == ChannelRuntimeState.Receiving);
public double ActivityBarOpacity => IsReceiving ? 1.0 : 0.12;
```

Subscribe once to each system channel's `PropertyChanged` and raise `IsReceiving` and `ActivityBarOpacity` only when `ChannelViewModel.State` changes. Unsubscribe during system disposal. `ZoneViewModel` already observes its channels; extend that handler with the same derived properties. Assign each zone in a system the system's stable accent brush so related bars use one visual identity.

Raise `StatusGlyph` when connection status changes. Replace `SystemTabText` with separate elements and a non-animated activity bar:

```xml
<StackPanel Spacing="3">
  <StackPanel Orientation="Horizontal" Spacing="5">
    <TextBlock Text="{Binding Name}" />
    <TextBlock Foreground="{Binding StatusAccentBrush}" Text="{Binding StatusGlyph}" />
  </StackPanel>
  <Border Height="3" CornerRadius="2"
          Background="{Binding StatusAccentBrush}"
          Opacity="{Binding ActivityBarOpacity}" />
</StackPanel>
```

Give the zone header the same lower bar bound to `ZoneViewModel.ActivityBrush` and `ActivityBarOpacity`. Keep Avalonia's selected-tab treatment intact; do not animate or flash the receive bar.

- [ ] **Step 4: Run system tests and desktop build**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~SystemAccentPalette|FullyQualifiedName~LoadsVariableSystemTabs|FullyQualifiedName~ReceiveActivity" /m:1 /p:UseSharedCompilation=false`

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 5: Commit the accent change**

```bash
git add src/DvmConsole.Desktop/SystemAccentPalette.cs src/DvmConsole.Desktop/MainWindow.axaml src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop.Tests/SystemAccentPaletteTests.cs src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs src/DvmConsole.Desktop.Tests/ZoneViewModelTests.cs
git commit -m "feat: show system and zone receive activity"
```

### Task 5: Make the global PTT key optional and add F13-F19

**Files:**
- Modify: `src/DvmConsole.Audio/KeyboardPttSource.cs:1-22`
- Modify: `src/DvmConsole.Audio/GlobalKeyboardPttSource.cs:615-660`
- Modify: `src/DvmConsole.Core/Settings/UserSettings.cs:65-72,714-730`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml:82-100`
- Modify: `src/DvmConsole.Desktop/MainWindow.axaml.cs:950-1000,1205,2350-2370,3180-3378,5915-5920`
- Modify: `src/DvmConsole.Desktop/OperatorToolsWindow.axaml:382-395`
- Test: `src/DvmConsole.Audio.Tests/KeyboardPttSourceTests.cs`
- Test: `src/DvmConsole.Core.Tests/UserSettingsStoreTests.cs`
- Test: `src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs`

**Interfaces:**
- Adds `KeyboardPttKey.None` and portable `F13` through `F19` values.
- Produces one `KeyboardPttKeyMapping` used by Windows virtual keys, macOS key codes, and Avalonia focused-window keys.
- Produces a persisted `GlobalPttKey` of `None`, `Space`, or `F1`-`F19`.

- [ ] **Step 1: Write failing mapping, settings, and disabled-state tests**

Extend the keyboard tests with Windows F13/F19 and macOS F13/F19 mappings, plus a theory covering every enum value from F1 through F19. In desktop tests, assert `GlobalPttKeyOptions` starts with None and contains F13-F19. Add a disabled test that applies None, confirms `GlobalPttKeyText == "Keyboard PTT disabled"`, confirms focused key events do not trigger PTT, and confirms serial/on-screen sources remain available.

In settings tests, assert `F19` round-trips, `None` round-trips, a missing value preserves the existing new-install Space default, and an unsupported explicit value normalizes to None rather than unexpectedly assigning Space.

- [ ] **Step 2: Run the focused tests and observe the missing enum values**

Run: `dotnet test src/DvmConsole.Audio.Tests/DvmConsole.Audio.Tests.csproj --no-restore --filter FullyQualifiedName~KeyboardPttSourceTests /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Core.Tests/DvmConsole.Core.Tests.csproj --no-restore --filter FullyQualifiedName~GlobalPtt /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~GlobalPtt /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because None and F13-F19 are not represented or mapped.

- [ ] **Step 3: Extend the portable binding and platform maps**

Add `None` and F13-F19 to `KeyboardPttKey`. Extend Windows mapping through virtual key `0x82` and macOS mapping through the platform function-key codes. Extend `MainWindow.TryMapPttKey` through Avalonia `Key.F19`. Keep mapping tests as the source of truth; do not infer function-key values arithmetically on macOS.

Change settings normalization to accept only None, Space, and F1-F19. Preserve Space as the new-install default and preserve all valid existing Space/F1-F12 values; None is the explicit no-binding value.

- [ ] **Step 4: Make source replacement safe and expose the complete choices**

Before changing a binding, force-release keyboard-owned PTT and dispose the old OS-global source. When the new value is None, retain a non-started focused `KeyboardPttSource` sentinel but do not create an OS event tap. Do not alter on-screen PTT or serial PTT configuration.

Generate the main-window submenu from `GlobalPttKeyOptions` so it cannot drift from Console Settings. Include a checked current value, label None as `None (keyboard PTT disabled)`, and include Space/F1-F19. Persist and refresh `GlobalPttKeyText`, `GlobalPttKeyOptions`, selection, tooltips, and status text immediately.

- [ ] **Step 5: Run all keyboard and settings tests**

Run: `dotnet test src/DvmConsole.Audio.Tests/DvmConsole.Audio.Tests.csproj --no-restore --filter FullyQualifiedName~KeyboardPtt /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Core.Tests/DvmConsole.Core.Tests.csproj --no-restore --filter FullyQualifiedName~GlobalPtt /m:1 /p:UseSharedCompilation=false`

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~GlobalPtt /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 6: Commit the global PTT binding change**

```bash
git add src/DvmConsole.Audio/KeyboardPttSource.cs src/DvmConsole.Audio/GlobalKeyboardPttSource.cs src/DvmConsole.Core/Settings/UserSettings.cs src/DvmConsole.Desktop/MainWindow.axaml src/DvmConsole.Desktop/MainWindow.axaml.cs src/DvmConsole.Desktop/OperatorToolsWindow.axaml src/DvmConsole.Audio.Tests/KeyboardPttSourceTests.cs src/DvmConsole.Core.Tests/UserSettingsStoreTests.cs src/DvmConsole.Desktop.Tests/SystemViewModelTests.cs
git commit -m "feat: expand optional global PTT keys"
```

### Task 6: Set and verify the macOS application name

**Files:**
- Modify: `src/DvmConsole.Desktop/App.axaml:1-4`
- Modify: `scripts/package-desktop.sh:55-100`
- Create: `src/DvmConsole.Desktop.Tests/AppIdentityTests.cs`

**Interfaces:**
- Produces: `Application.Name == "DVM Console"` after AXAML initialization.
- Consumes: existing `CFBundleName` and `CFBundleDisplayName` in `packaging/macos/Info.plist`.

- [ ] **Step 1: Write the failing Avalonia application-name test**

```csharp
[Fact]
public void AvaloniaApplicationNameMatchesProductName()
{
    var app = new App();
    app.Initialize();

    Assert.Equal("DVM Console", app.Name);
}
```

- [ ] **Step 2: Run the test and verify the current fallback**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~AppIdentityTests /m:1 /p:UseSharedCompilation=false`

Expected: FAIL because `Application.Name` is unset.

- [ ] **Step 3: Set the name and strengthen packaging validation**

Set the AXAML root property:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="DvmConsole.Desktop.App"
             Name="DVM Console">
```

In `package-desktop.sh`, read and validate both keys after copying the plist:

```bash
bundle_name=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleName' "$APP_PATH/Contents/Info.plist")
bundle_display_name=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleDisplayName' "$APP_PATH/Contents/Info.plist")
if [[ "$bundle_name" != "DVM Console" || "$bundle_display_name" != "DVM Console" ]]; then
    printf 'macOS bundle identity must be DVM Console.\n' >&2
    exit 12
fi
```

- [ ] **Step 4: Verify test, build, plist, and a macOS smoke launch**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore --filter FullyQualifiedName~AppIdentityTests /m:1 /p:UseSharedCompilation=false`

Run: `plutil -lint packaging/macos/Info.plist`

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

On macOS, run the existing desktop smoke launch and manually verify the bold application menu reads `DVM Console`, not `Avalonia Application`.

Expected: automated checks PASS and menu identity is correct.

- [ ] **Step 5: Commit application identity**

```bash
git add src/DvmConsole.Desktop/App.axaml scripts/package-desktop.sh src/DvmConsole.Desktop.Tests/AppIdentityTests.cs
git commit -m "fix: name the macOS application menu"
```

### Task 7: Validate the complete channel-controls slice

**Files:**
- No additional source files.

**Interfaces:**
- Consumes all deliverables in Tasks 1-6.

- [ ] **Step 1: Run the focused desktop suite**

Run: `dotnet test src/DvmConsole.Desktop.Tests/DvmConsole.Desktop.Tests.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Expected: PASS.

- [ ] **Step 2: Run the desktop build**

Run: `dotnet build src/DvmConsole.Desktop/DvmConsole.Desktop.csproj --no-restore /m:1 /p:UseSharedCompilation=false`

Expected: PASS with zero warnings.

- [ ] **Step 3: Perform operator input validation**

Validate TAR, TX, PAGE, and ALERT by clicking text, icon, padding, and border while widgets are both locked and unlocked. Confirm no click toggles receive, colors change before the pointer leaves, and hover across each control does not flicker. Validate all four receive menu actions against two systems and two zones. Confirm system dots are distinct and stable after relaunch. With concurrent calls on selected and unselected systems/zones, confirm every affected activity bar turns on immediately and clears at call end while selection and connection visuals remain unchanged.

- [ ] **Step 4: Commit any test-only corrections**

```bash
git add src/DvmConsole.Desktop src/DvmConsole.Desktop.Tests scripts/package-desktop.sh
git commit -m "test: validate channel controls and identity"
```
