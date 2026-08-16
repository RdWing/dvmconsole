# Settings Reference

This page summarizes user-facing settings in the console.

---

# Settings Menu

## Toggle Push To Talk Mode

When enabled, clicking PTT toggles transmit on or off.

When disabled, PTT behaves as press-and-hold.

Default:

```
Off
```

This setting is saved.

## Talk Permit Tone

When enabled, the console plays a short local tone when transmit begins.

This is local operator feedback only.

## Connection Chimes

Connection chimes are enabled by default. Disable this setting if the operator
does not want local audible feedback when an FNE connects or disconnects.

## Interface Size

Open **Settings > All console settings** and use the Appearance controls to
adjust the console display.

- **Text size** changes the inherited application font size.
- **Interface scale** scales the complete main console and Console Settings UI.

Both settings apply immediately and are saved for the next launch.

## Mute RX Audio While Transmitting

When enabled, local RX speaker playback is muted while the console is transmitting.

This does not block received network traffic, logs, patch forwarding, or RX visual state. It only suppresses local speaker playback during TX.

Use this when operators use speakers and you want to reduce the chance of received audio feeding back into the microphone.

## Retain Patch State on Startup

When enabled, patch active/on-off state is restored on startup.

Patch members always persist. This setting only controls whether enabled patches come back enabled after restart.

Default:

```
Off
```

## Restore Selected Channels On Startup

When enabled, selected resources are restored on startup.

Restored selected encrypted resources request keys after the relevant FNE connection is established and a short delay has passed.

When disabled, selected resources and per-resource volume do not come back sticky on startup.

## Toolbar Clocks

Open **Settings > Clock settings** to show the General page in Console Settings. Up to eight toolbar clocks can be enabled.

Each clock has:

- an enable/disable checkbox
- a UTC offset, such as `UTC+00`, `UTC-05`, or `UTC+09`
- a preset box color for visual grouping

The General page also controls the shared clock display format:

- `Use 24-hour time`
- `Show seconds`

Clock settings are saved and restored on startup. Enabled clock slots, UTC offsets, colors, 12/24-hour mode, and seconds display are all sticky user preferences.

## Audio Settings

Opens Console Settings on the Audio page.

See **Audio Settings** for details.

## Import / Export Settings

Opens the Settings Transfer window.

Use this to move console preferences between machines without manually copying `UserSettings.json`.

The transfer file is a portable JSON file. You can choose which categories to export or import, including:

- console layout and widget positions
- audio routing and volumes
- TAR configuration
- patch and multi-select group state
- custom alert tones
- toolbar clocks
- startup restore state
- operator preferences
- call history window preferences
- keybinds and selectable encryption state

Press `Ctrl+A` in the transfer window to select all categories.

On import, only the selected categories are replaced. The console reloads the current codeplug/widgets after import so layout and routing changes take effect immediately.

## Reset Settings

Clears saved user settings.

Use with care. This can remove saved window layout, widget positions, audio routing, selected channel state, and other preferences.

## Widgets

The General page can show or hide system status cards, channel widgets, and toolbar alert buttons. **Lock channel widget positions** prevents card dragging; clear it to arrange cards on the active tab.

## Global PTT Key

Choose Space or F1 through F12 from **Channels > Global PTT key**.

---

# View Menu

## Select User Background

Chooses a custom background image for the main console.

## Dark Mode

Toggles the app theme.

## Lock Widgets

Prevents resource and status widgets from being moved.

## Reset Widget Layout

Snaps channel cards back to a grid-style layout.

## Event History

The Event History can be docked in the collapsible Activity sidebar or opened as a separate window. **Snap detached Event History** keeps the detached window aligned with the main console.

## Groups

Opens Console Settings on the Groups page.

## Keep Window on Top

Keeps the console above other windows.

---

# Tools Menu

## Talkgroup Audio Recorder

Open from:

```
Tools > Talkgroup Audio Recorder
```

Sub-items:

- Viewer
- Configuration

See **Configurations > Talkgroup Audio Recorder** for TAR recording, retention, playback, filtering, and retention details.

## FNE Connection Manager

Opens manual connection controls for configured FNE systems.

---

# Help Menu

## Documentation

Opens the searchable documentation viewer. Pages are read live from the
`avalonia_v2` documentation tree on GitHub and rendered with headings, lists,
tables, links, and code blocks. An internet connection is required; Markdown is
not bundled into the application package.

## About

Shows version `0.1.0` and the seven-character commit ID, copyright, AGPL license notice, project attribution, and repository links.
