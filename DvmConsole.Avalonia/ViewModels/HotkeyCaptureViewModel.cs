// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.ComponentModel;
using DvmConsole.Platform.Hotkeys;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Pure managed hotkey-capture state slice for the operator
    /// dashboard: the capture-in-progress flag plus the apply/clear
    /// entry points that forward the captured gesture to the composed
    /// <see cref="PttCapabilityViewModel"/>. With a null Ptt slice every
    /// method is a no-op and <see cref="IsCapturing"/> stays false.
    /// With a Ptt slice: <see cref="StartCapture"/> and
    /// <see cref="Cancel"/> flip <see cref="IsCapturing"/> change-only;
    /// <see cref="ApplyKey"/> acts only while capturing and only for a
    /// non-None gesture — calling <c>Ptt.SetHotkey</c> exactly once,
    /// then exiting capture with one IsCapturing-false notification
    /// (idle and None-key calls are no-ops); and
    /// <see cref="ClearHotkey"/> always calls <c>Ptt.ClearHotkey</c> —
    /// even while idle — then cancels capture change-only. This slice
    /// never registers, unregisters, or disposes anything on the hotkey
    /// service and is deliberately not <see cref="System.IDisposable"/>.
    /// </summary>
    public sealed class HotkeyCaptureViewModel : INotifyPropertyChanged
    {
        private readonly PttCapabilityViewModel? ptt;
        private bool isCapturing;

        /// <summary>
        /// Creates the hotkey-capture slice. A null Ptt slice disables
        /// the slice: every method becomes a no-op. No service query is
        /// performed at construction.
        /// </summary>
        /// <param name="ptt">The composed PTT capability slice, or null to disable capture.</param>
        public HotkeyCaptureViewModel(PttCapabilityViewModel? ptt)
        {
            this.ptt = ptt;
        }

        /// <summary>True while hotkey capture is in progress.</summary>
        public bool IsCapturing => isCapturing;

        /// <summary>
        /// Begins hotkey capture. Raises <see cref="PropertyChanged"/>
        /// for <see cref="IsCapturing"/> only when the value changes; a
        /// repeated call while already capturing is silent. No-op with a
        /// null Ptt slice.
        /// </summary>
        public void StartCapture()
        {
            if (ptt is null || isCapturing)
            {
                return;
            }

            isCapturing = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCapturing)));
        }

        /// <summary>
        /// Cancels hotkey capture. Raises <see cref="PropertyChanged"/>
        /// for <see cref="IsCapturing"/> only when the value changes; a
        /// repeated call while already idle is silent. No-op with a null
        /// Ptt slice.
        /// </summary>
        public void Cancel()
        {
            if (ptt is null || !isCapturing)
            {
                return;
            }

            isCapturing = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCapturing)));
        }

        /// <summary>
        /// Applies a captured gesture while capturing: forwards it to
        /// <c>Ptt.SetHotkey</c> exactly once, exits capture, and raises
        /// exactly one <see cref="IsCapturing"/>-false notification.
        /// While idle, or for a <see cref="HotkeyKey.None"/>-key gesture,
        /// this is a no-op that leaves capture (and Ptt) untouched.
        /// No-op with a null Ptt slice.
        /// </summary>
        /// <param name="gesture">The captured gesture to apply.</param>
        public void ApplyKey(HotkeyGesture gesture)
        {
            if (ptt is null || !isCapturing || gesture.Key == HotkeyKey.None)
            {
                return;
            }

            ptt.SetHotkey(gesture);
            isCapturing = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCapturing)));
        }

        /// <summary>
        /// Clears the configured hotkey: always calls
        /// <c>Ptt.ClearHotkey</c> — even while idle — and cancels
        /// capture change-only (no notification when already idle).
        /// No-op with a null Ptt slice.
        /// </summary>
        public void ClearHotkey()
        {
            if (ptt is null)
            {
                return;
            }

            ptt.ClearHotkey();
            Cancel();
        }

        /// <summary>Raised when any property value changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
