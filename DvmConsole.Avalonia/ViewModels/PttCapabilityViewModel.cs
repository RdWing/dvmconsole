// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using DvmConsole.Platform.Hotkeys;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Pure managed PTT capability and engagement state slice for the
    /// operator dashboard: the configured hotkey gesture plus its
    /// platform capability, the momentary/toggle PTT mode, and the
    /// engagement state shared by the pointer and hotkey paths.
    ///
    /// This view-model is intentionally pure managed state: it never
    /// registers, unregisters, or disposes anything on the hotkey
    /// service — <see cref="IGlobalHotkeyService.GetCapability"/> is the
    /// only service query, and it happens only for the currently
    /// configured gesture. The shell feeds hotkey events in through
    /// <see cref="ApplyHotkeyPress"/> and reacts to engagement and
    /// hotkey-change requests through <see cref="PttStateRequested"/>
    /// and <see cref="HotkeyChangeRequested"/>.
    /// </summary>
    public sealed class PttCapabilityViewModel : INotifyPropertyChanged
    {
        private readonly IGlobalHotkeyService hotkeys;
        private readonly Func<ChannelSlotViewModel?> primaryChannel;
        private readonly Func<IReadOnlyCollection<ChannelSlotViewModel>> selectedChannels;

        private HotkeyGesture? hotkey;
        private HotkeyCapability capability = HotkeyCapability.Unsupported;
        private bool toggleMode;
        private bool allChannels;
        private bool isEngaged;

        /// <summary>
        /// Hotkey key-down latch: armed by an accepted matching
        /// <see cref="HotkeyEventType.Pressed"/> and cleared by the
        /// matching <see cref="HotkeyEventType.Released"/>, a watchdog
        /// key-up detection, or a hotkey Clear/Set that changes the
        /// configured gesture. Guards the hotkey path against repeat
        /// presses (notably the toggle-off of a repeat press in toggle
        /// mode) and feeds the key-up watchdog.
        /// </summary>
        private bool hotkeyDownLatched;

        /// <summary>The press-time target snapshot of the current engagement; null while released.</summary>
        private IReadOnlyList<ChannelSlotViewModel>? engagedTargets;

        /// <summary>True when the current engagement was started while in toggle mode.</summary>
        private bool engagedFromToggle;

        /// <summary>
        /// Creates the PTT capability state slice. No service query is
        /// performed until a gesture is configured via
        /// <see cref="SetHotkey"/>.
        /// </summary>
        /// <param name="hotkeys">The platform hotkey service (capability queries only).</param>
        /// <param name="primaryChannel">
        /// Resolves the primary channel slot at press time; wins over
        /// the selected channels whenever it returns a slot.
        /// </param>
        /// <param name="selectedChannels">
        /// Resolves the selected channel slots at press time; used when
        /// <see cref="AllChannels"/> is true and the primary resolver
        /// returned none.
        /// </param>
        public PttCapabilityViewModel(
            IGlobalHotkeyService hotkeys,
            Func<ChannelSlotViewModel?> primaryChannel,
            Func<IReadOnlyCollection<ChannelSlotViewModel>> selectedChannels)
        {
            this.hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
            this.primaryChannel = primaryChannel ?? throw new ArgumentNullException(nameof(primaryChannel));
            this.selectedChannels = selectedChannels ?? throw new ArgumentNullException(nameof(selectedChannels));
        }

        /// <summary>The configured hotkey gesture, or null while none is set.</summary>
        public HotkeyGesture? Hotkey => hotkey;

        /// <summary>
        /// The platform capability of the configured gesture;
        /// <see cref="HotkeyCapability.Unsupported"/> while no gesture is
        /// configured or after <see cref="ClearHotkey"/>.
        /// </summary>
        public HotkeyCapability Capability => capability;

        /// <summary>
        /// True when PTT toggles on each press; false for press-and-hold
        /// momentary operation. Raises <see cref="PropertyChanged"/> only
        /// when the value changes.
        /// </summary>
        public bool ToggleMode
        {
            get => toggleMode;
            set
            {
                if (toggleMode == value)
                {
                    return;
                }

                toggleMode = value;
                RaisePropertyChanged(nameof(ToggleMode));
                RaiseSaveRequested();
            }
        }

        /// <summary>
        /// True when PTT targets resolve from the selected-channels
        /// resolver instead of the primary channel. Raises
        /// <see cref="PropertyChanged"/> only when the value changes.
        /// </summary>
        public bool AllChannels
        {
            get => allChannels;
            set
            {
                if (allChannels == value)
                {
                    return;
                }

                allChannels = value;
                RaisePropertyChanged(nameof(AllChannels));
                RaiseSaveRequested();
            }
        }

        /// <summary>
        /// True while PTT is engaged, whether by the pointer or the
        /// hotkey path. Raises <see cref="PropertyChanged"/> only when
        /// the value changes.
        /// </summary>
        public bool IsEngaged => isEngaged;

        /// <summary>
        /// The press-time target snapshot of the current engagement
        /// (the primary channel, or the reference-deduplicated
        /// AllChannels selection), or null while released. Get-only:
        /// populated by engagement and cleared by release — never
        /// re-resolved mid-engagement, so a release always releases
        /// exactly the pressed targets. Consumed by the shell to fan
        /// the PTT out to every engaged target.
        /// </summary>
        public IReadOnlyList<ChannelSlotViewModel>? EngagedTargets => engagedTargets;

        /// <summary>
        /// Assigns the hotkey gesture, queries its capability exactly
        /// once, raises <see cref="Hotkey"/> / <see cref="Capability"/>
        /// change-only notifications, and raises
        /// <see cref="HotkeyChangeRequested"/> exactly once (even when
        /// the gesture is unchanged). Rejects
        /// <see cref="HotkeyKey.None"/> with <see cref="ArgumentException"/>.
        /// </summary>
        public void SetHotkey(HotkeyGesture gesture)
        {
            if (gesture.Key == HotkeyKey.None)
            {
                throw new ArgumentException("The hotkey key must not be None.", nameof(gesture));
            }

            var hotkeyChanged = hotkey is not { } existing || existing != gesture;
            hotkey = gesture;

            if (hotkeyChanged)
            {
                // A changed gesture invalidates any in-flight key-down
                // latch; the old gesture's key is no longer watched.
                hotkeyDownLatched = false;
            }

            var newCapability = hotkeys.GetCapability(gesture);
            var capabilityChanged = newCapability != capability;
            capability = newCapability;

            if (hotkeyChanged)
            {
                RaisePropertyChanged(nameof(Hotkey));
            }

            if (capabilityChanged)
            {
                RaisePropertyChanged(nameof(Capability));
            }

            if (hotkeyChanged)
            {
                RaiseSaveRequested();
            }

            HotkeyChangeRequested?.Invoke(gesture);
        }

        /// <summary>
        /// Clears the hotkey: resets <see cref="Hotkey"/> to null and
        /// <see cref="Capability"/> to
        /// <see cref="HotkeyCapability.Unsupported"/> (change-only
        /// notifications, never querying the service) and raises
        /// <see cref="HotkeyChangeRequested"/> with null. No-op while
        /// already cleared.
        /// </summary>
        public void ClearHotkey()
        {
            if (hotkey is null)
            {
                return;
            }

            hotkey = null;
            hotkeyDownLatched = false;
            var capabilityChanged = capability != HotkeyCapability.Unsupported;
            capability = HotkeyCapability.Unsupported;

            RaisePropertyChanged(nameof(Hotkey));

            if (capabilityChanged)
            {
                RaisePropertyChanged(nameof(Capability));
            }

            RaiseSaveRequested();
            HotkeyChangeRequested?.Invoke(null);
        }

        /// <summary>
        /// Pointer down: engages the press-time target snapshot in
        /// momentary mode, toggles engagement in toggle mode, and is
        /// idempotent while already engaged in momentary mode.
        /// </summary>
        public void PttPointerDown() => PressDown();

        /// <summary>
        /// Pointer up: releases exactly the press-time snapshot in
        /// momentary mode; no-op in toggle mode and while already
        /// released.
        /// </summary>
        public void PttPointerUp()
        {
            if (!engagedFromToggle)
            {
                Release();
            }
        }

        internal void CancelEngagement()
        {
            hotkeyDownLatched = false;
            engagedFromToggle = false;
            Release();
        }

        /// <summary>
        /// Routes a hotkey event from the shell. Acts only when a
        /// gesture is configured and equals the incoming one: momentary
        /// maps Pressed to a down and Released to an up; toggle maps
        /// Pressed to a down and ignores Released. Mismatched or
        /// unconfigured gestures are no-ops. An accepted matching
        /// Pressed arms the hotkey key-down latch; repeat Pressed
        /// events while the latch is armed are ignored in both modes,
        /// and a matching Released always clears the latch (releasing
        /// PTT only in momentary mode).
        /// </summary>
        public void ApplyHotkeyPress(HotkeyGesture gesture, HotkeyEventType eventType)
        {
            if (hotkey is not { } configured || configured != gesture)
            {
                return;
            }

            if (eventType == HotkeyEventType.Pressed)
            {
                if (hotkeyDownLatched)
                {
                    return;
                }

                hotkeyDownLatched = true;
                PressDown();
            }
            else
            {
                hotkeyDownLatched = false;

                if (!engagedFromToggle)
                {
                    Release();
                }
            }
        }

        /// <summary>
        /// Key-up watchdog tick driven by the shell's physical key-state
        /// poll. Self-contained: no-op while the hotkey key-down latch is
        /// clear or the key is physically down. When the latch is armed
        /// and the key is physically up, the missed key-up is resolved:
        /// the latch clears and, for an engagement started in momentary
        /// mode, the idempotent release is forced and
        /// <see cref="KeyUpMissed"/> is raised exactly once; a toggle
        /// engagement only clears the latch — it stays engaged and no
        /// signal is raised. Never touches the pointer path.
        /// </summary>
        internal void WatchdogTick(bool keyIsPhysicallyDown)
        {
            if (!hotkeyDownLatched || keyIsPhysicallyDown)
            {
                return;
            }

            hotkeyDownLatched = false;

            if (!engagedFromToggle)
            {
                Release();
                KeyUpMissed?.Invoke();
            }
        }

        /// <summary>
        /// Engages in momentary mode, toggles in toggle mode. A repeated
        /// down while engaged in momentary mode is an idempotent no-op
        /// that never re-resolves the targets.
        /// </summary>
        private void PressDown()
        {
            if (isEngaged)
            {
                if (engagedFromToggle)
                {
                    Release();
                }

                return;
            }

            Engage();
        }

        /// <summary>
        /// Engages the press-time target snapshot: each target reports
        /// PttEngaged, IsEngaged becomes true, and
        /// <see cref="PttStateRequested"/> is raised with true. No-op
        /// when there is no target.
        /// </summary>
        private void Engage()
        {
            var targets = ResolveTargets();
            if (targets is null)
            {
                return;
            }

            engagedTargets = targets;
            engagedFromToggle = toggleMode;

            foreach (var slot in targets)
            {
                slot.PttEngaged = true;
            }

            isEngaged = true;
            RaisePropertyChanged(nameof(IsEngaged));
            PttStateRequested?.Invoke(true);
        }

        /// <summary>
        /// Releases exactly the press-time snapshot: each engaged target
        /// reports PttEngaged=false, IsEngaged becomes false, and
        /// <see cref="PttStateRequested"/> is raised with false.
        /// Idempotent while already released.
        /// </summary>
        private void Release()
        {
            if (!isEngaged)
            {
                return;
            }

            if (engagedTargets is not null)
            {
                foreach (var slot in engagedTargets)
                {
                    slot.PttEngaged = false;
                }
            }

            engagedTargets = null;
            isEngaged = false;
            RaisePropertyChanged(nameof(IsEngaged));
            PttStateRequested?.Invoke(false);
        }

        /// <summary>
        /// Resolves the press-time target snapshot: the primary channel
        /// wins when it resolves; an RX-only primary blocks the press
        /// rather than falling through to <see cref="AllChannels"/>.
        /// With no primary, AllChannels resolves only PTT-eligible
        /// selected channels; otherwise there is no target (null).
        /// Duplicate slot references are de-duplicated by reference,
        /// preserving order.
        /// </summary>
        private IReadOnlyList<ChannelSlotViewModel>? ResolveTargets()
        {
            var primary = primaryChannel();
            if (primary is not null)
            {
                return primary.IsPttEnabled ? new[] { primary } : null;
            }

            if (!allChannels)
            {
                return null;
            }

            var targets = new List<ChannelSlotViewModel>();
            foreach (var slot in selectedChannels())
            {
                if (slot is { IsPttEnabled: true } && !targets.Contains(slot))
                {
                    targets.Add(slot);
                }
            }

            return targets.Count == 0 ? null : targets;
        }

        private void RaisePropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        /// <summary>Raised when any property value changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Raised with the requested PTT engagement state (true = engage, false = release).</summary>
        public event Action<bool>? PttStateRequested;

        /// <summary>
        /// Raised exactly once when the key-up watchdog resolves a
        /// missed hotkey key-up in momentary mode (the idempotent
        /// release was forced because the physical key came up without
        /// a matching <see cref="HotkeyEventType.Released"/>). Internal:
        /// consumed by the shell; not part of the public PTT surface.
        /// </summary>
        internal event Action? KeyUpMissed;

        /// <summary>Raised with the requested hotkey gesture (null = clear).</summary>
        public event Action<HotkeyGesture?>? HotkeyChangeRequested;

        /// <summary>
        /// Raised exactly once for each effective persisted PTT change:
        /// a changed or cleared hotkey, a changed toggle-mode value, or a
        /// changed all-channels value. The payload is the current complete
        /// PTT state; no event is raised for no-op assignments. Hydration is
        /// silent when the consumer subscribes after construction.
        /// </summary>
        public event Action<HotkeyGesture?, bool, bool>? SaveRequested;

        private void RaiseSaveRequested()
            => SaveRequested?.Invoke(hotkey, toggleMode, allChannels);
    }
}
