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
            }
        }

        /// <summary>
        /// True while PTT is engaged, whether by the pointer or the
        /// hotkey path. Raises <see cref="PropertyChanged"/> only when
        /// the value changes.
        /// </summary>
        public bool IsEngaged => isEngaged;

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
            var capabilityChanged = capability != HotkeyCapability.Unsupported;
            capability = HotkeyCapability.Unsupported;

            RaisePropertyChanged(nameof(Hotkey));

            if (capabilityChanged)
            {
                RaisePropertyChanged(nameof(Capability));
            }

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

        /// <summary>
        /// Routes a hotkey event from the shell. Acts only when a
        /// gesture is configured and equals the incoming one: momentary
        /// maps Pressed to a down and Released to an up; toggle maps
        /// Pressed to a down and ignores Released. Mismatched or
        /// unconfigured gestures are no-ops.
        /// </summary>
        public void ApplyHotkeyPress(HotkeyGesture gesture, HotkeyEventType eventType)
        {
            if (hotkey is not { } configured || configured != gesture)
            {
                return;
            }

            if (eventType == HotkeyEventType.Pressed)
            {
                PressDown();
            }
            else if (!engagedFromToggle)
            {
                Release();
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
        /// wins when it resolves; otherwise <see cref="AllChannels"/>
        /// resolves the selected channels; otherwise there is no target
        /// (null). Duplicate slot references are de-duplicated by
        /// reference, preserving order.
        /// </summary>
        private IReadOnlyList<ChannelSlotViewModel>? ResolveTargets()
        {
            var primary = primaryChannel();
            if (primary is not null)
            {
                return new[] { primary };
            }

            if (!allChannels)
            {
                return null;
            }

            var targets = new List<ChannelSlotViewModel>();
            foreach (var slot in selectedChannels())
            {
                if (slot is not null && !targets.Contains(slot))
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

        /// <summary>Raised with the requested hotkey gesture (null = clear).</summary>
        public event Action<HotkeyGesture?>? HotkeyChangeRequested;
    }
}
