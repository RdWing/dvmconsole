// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.ComponentModel;
using DvmConsole.Avalonia.Persistence;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Pure managed operator-preferences state. The optional persistence
    /// adapter is used only to hydrate the six WPF-compatible values during
    /// construction; post-hydration changes raise <see cref="SaveRequested"/>
    /// for the owning dashboard view-model to persist.
    /// </summary>
    /// <remarks>
    /// The shell consumes the theme and always-on-top values; the remaining
    /// preference consumers stay behind their existing request-only seams.
    /// </remarks>
    public sealed class OperatorPreferencesViewModel : INotifyPropertyChanged
    {
        private bool talkPermitTone;
        private bool muteRxAudioWhileTransmitting;
        private bool retainPatchStateOnStartup;
        private bool restoreSelectedChannelsOnStartup;
        private bool darkMode;
        private bool keepWindowOnTop;

        /// <summary>
        /// Loads persisted values when the adapter is present. Missing,
        /// malformed, or unreadable settings degrade to the six false
        /// defaults without throwing.
        /// </summary>
        public OperatorPreferencesViewModel(PreferencesSettingsPersistence? persistence)
        {
            if (persistence is null)
            {
                return;
            }

            try
            {
                if (persistence.TryLoad(out UserSettingsPreferencesSection section))
                {
                    talkPermitTone = section.TalkPermitTone;
                    muteRxAudioWhileTransmitting = section.MuteRxAudioWhileTransmitting;
                    retainPatchStateOnStartup = section.RetainPatchStateOnStartup;
                    restoreSelectedChannelsOnStartup = section.RestoreSelectedChannelsOnStartup;
                    darkMode = section.DarkMode;
                    keepWindowOnTop = section.KeepWindowOnTop;
                }
            }
            catch
            {
                // Persistence must never break dashboard construction.
            }
        }

        /// <summary>Raised when a preference property changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Raised once for each effective post-hydration change.</summary>
        public event Action? SaveRequested;

        /// <summary>Whether local talk-permit tone playback is enabled.</summary>
        public bool TalkPermitTone
        {
            get => talkPermitTone;
            set => Set(ref talkPermitTone, value, nameof(TalkPermitTone));
        }

        /// <summary>Whether receive playback is muted during transmission.</summary>
        public bool MuteRxAudioWhileTransmitting
        {
            get => muteRxAudioWhileTransmitting;
            set => Set(ref muteRxAudioWhileTransmitting, value, nameof(MuteRxAudioWhileTransmitting));
        }

        /// <summary>Whether patch state is retained across startup.</summary>
        public bool RetainPatchStateOnStartup
        {
            get => retainPatchStateOnStartup;
            set => Set(ref retainPatchStateOnStartup, value, nameof(RetainPatchStateOnStartup));
        }

        /// <summary>Whether valid selected channels are restored at startup.</summary>
        public bool RestoreSelectedChannelsOnStartup
        {
            get => restoreSelectedChannelsOnStartup;
            set => Set(ref restoreSelectedChannelsOnStartup, value, nameof(RestoreSelectedChannelsOnStartup));
        }

        /// <summary>Whether the dark shell theme is selected.</summary>
        public bool DarkMode
        {
            get => darkMode;
            set => Set(ref darkMode, value, nameof(DarkMode));
        }

        /// <summary>Whether the shell should remain above other windows.</summary>
        public bool KeepWindowOnTop
        {
            get => keepWindowOnTop;
            set => Set(ref keepWindowOnTop, value, nameof(KeepWindowOnTop));
        }

        private void Set(ref bool field, bool value, string propertyName)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            SaveRequested?.Invoke();
        }
    }
}
