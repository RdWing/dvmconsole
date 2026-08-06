// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Explicit-refresh view-model for the audio settings panel. The
    /// constructor and <see cref="Refresh"/> rebuild read-only device
    /// option lists from the <see cref="IAudioDeviceCatalog"/>: the
    /// available system-default row first, then catalog devices in source
    /// order with case-insensitive id dedup (first occurrence wins) and
    /// default-id entries excluded. Selection ids are nullable and
    /// reconcile against the current catalog per direction: a saved id
    /// matching a device case-insensitively selects the canonical catalog
    /// id, while a saved id absent from its direction is appended after the
    /// real devices as exactly one unavailable row with a neutral name and
    /// the selection stays the saved id. All property changes raise
    /// change-only <see cref="PropertyChanged"/> notifications;
    /// <see cref="Commit"/> raises <see cref="SaveRequested"/> exactly once
    /// per call with the current selection (falling back to
    /// <see cref="AudioDeviceId.Default"/>) and AgcEnabled, with no
    /// persistence and no state mutation. This slice is explicit-refresh by
    /// design: no catalog event subscription, no IDisposable surface, and
    /// no native code — the UI/native layer marshals concrete catalog
    /// changes and calls <see cref="Refresh"/> itself.
    /// </summary>
    public sealed class AudioSettingsViewModel : INotifyPropertyChanged
    {
        private const string SystemDefaultInputName = "System Default Input";
        private const string SystemDefaultOutputName = "System Default Output";
        private const string UnavailableInputName =
            "Saved input device unavailable; using system default until it returns";
        private const string UnavailableOutputName =
            "Saved output device unavailable; using system default until it returns";

        private readonly IAudioDeviceCatalog catalog;

        private IReadOnlyList<AudioDeviceOptionViewModel> inputDevices;
        private IReadOnlyList<AudioDeviceOptionViewModel> outputDevices;
        private AudioDeviceId? selectedInputId;
        private AudioDeviceId? selectedOutputId;
        private bool agcEnabled;

        /// <summary>
        /// Snapshot the catalog and apply the saved selection/AGC state.
        /// </summary>
        /// <param name="catalog">Audio device catalog to snapshot; must not be null.</param>
        /// <param name="savedInputId">Saved input selection, or null to select the system default.</param>
        /// <param name="savedOutputId">Saved output selection, or null to select the system default.</param>
        /// <param name="agcEnabled">Initial automatic gain control state.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="catalog"/> is null.</exception>
        public AudioSettingsViewModel(
            IAudioDeviceCatalog catalog,
            AudioDeviceId? savedInputId = null,
            AudioDeviceId? savedOutputId = null,
            bool agcEnabled = false)
        {
            ArgumentNullException.ThrowIfNull(catalog);

            this.catalog = catalog;
            this.agcEnabled = agcEnabled;

            inputDevices = BuildDirection(
                catalog.GetInputs(),
                SystemDefaultInputName,
                UnavailableInputName,
                savedInputId ?? AudioDeviceId.Default,
                out selectedInputId);
            outputDevices = BuildDirection(
                catalog.GetOutputs(),
                SystemDefaultOutputName,
                UnavailableOutputName,
                savedOutputId ?? AudioDeviceId.Default,
                out selectedOutputId);
        }

        /// <summary>Raised when any displayed property changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Raised by <see cref="Commit"/> with the current selection and
        /// AGC state; the payload ids are never null.
        /// </summary>
        public event Action<AudioDeviceId, AudioDeviceId, bool>? SaveRequested;

        /// <summary>Read-only snapshot of selectable input devices.</summary>
        public IReadOnlyList<AudioDeviceOptionViewModel> InputDevices => inputDevices;

        /// <summary>Read-only snapshot of selectable output devices.</summary>
        public IReadOnlyList<AudioDeviceOptionViewModel> OutputDevices => outputDevices;

        /// <summary>Selected input device id, or null when none is selected.</summary>
        public AudioDeviceId? SelectedInputId
        {
            get => selectedInputId;
            set
            {
                if (value != selectedInputId)
                {
                    selectedInputId = value;
                    OnPropertyChanged(nameof(SelectedInputId));
                }
            }
        }

        /// <summary>Selected output device id, or null when none is selected.</summary>
        public AudioDeviceId? SelectedOutputId
        {
            get => selectedOutputId;
            set
            {
                if (value != selectedOutputId)
                {
                    selectedOutputId = value;
                    OnPropertyChanged(nameof(SelectedOutputId));
                }
            }
        }

        /// <summary>True when automatic gain control is enabled.</summary>
        public bool AgcEnabled
        {
            get => agcEnabled;
            set
            {
                if (value != agcEnabled)
                {
                    agcEnabled = value;
                    OnPropertyChanged(nameof(AgcEnabled));
                }
            }
        }

        /// <summary>
        /// Re-snapshot both device lists from the catalog, preserving the
        /// current selections case-insensitively (canonicalizing to the
        /// returned current catalog id when the device is present,
        /// re-appending the unavailable row while it is absent), and raise
        /// change-only notifications. AgcEnabled is never reset.
        /// </summary>
        public void Refresh()
        {
            var newInputs = BuildDirection(
                catalog.GetInputs(),
                SystemDefaultInputName,
                UnavailableInputName,
                selectedInputId,
                out var canonicalInput);
            var newOutputs = BuildDirection(
                catalog.GetOutputs(),
                SystemDefaultOutputName,
                UnavailableOutputName,
                selectedOutputId,
                out var canonicalOutput);

            inputDevices = newInputs;
            outputDevices = newOutputs;

            OnPropertyChanged(nameof(InputDevices));
            OnPropertyChanged(nameof(OutputDevices));

            if (canonicalInput != selectedInputId)
            {
                selectedInputId = canonicalInput;
                OnPropertyChanged(nameof(SelectedInputId));
            }

            if (canonicalOutput != selectedOutputId)
            {
                selectedOutputId = canonicalOutput;
                OnPropertyChanged(nameof(SelectedOutputId));
            }
        }

        /// <summary>
        /// Raise <see cref="SaveRequested"/> exactly once with the current
        /// selection (falling back to <see cref="AudioDeviceId.Default"/>)
        /// and AgcEnabled. No state is mutated and no notifications are
        /// raised.
        /// </summary>
        public void Commit()
        {
            SaveRequested?.Invoke(
                selectedInputId ?? AudioDeviceId.Default,
                selectedOutputId ?? AudioDeviceId.Default,
                agcEnabled);
        }

        private static IReadOnlyList<AudioDeviceOptionViewModel> BuildDirection(
            IReadOnlyList<AudioDeviceInfo>? devices,
            string defaultRowName,
            string unavailableRowName,
            AudioDeviceId? currentSelection,
            out AudioDeviceId? canonicalSelection)
        {
            var rows = new List<AudioDeviceOptionViewModel>
            {
                new AudioDeviceOptionViewModel(AudioDeviceId.Default, defaultRowName, isAvailable: true),
            };

            AudioDeviceId? canonical = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (devices is not null)
            {
                foreach (var info in devices)
                {
                    if (info is null || info.Id.IsDefault || !seen.Add(info.Id.Value))
                    {
                        continue;
                    }

                    rows.Add(new AudioDeviceOptionViewModel(info.Id, info.Name, isAvailable: true));

                    if (canonical is null
                        && currentSelection is { } wanted
                        && !wanted.IsDefault
                        && string.Equals(wanted.Value, info.Id.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        canonical = info.Id;
                    }
                }
            }

            if (currentSelection is { } selected && !selected.IsDefault)
            {
                if (canonical is null)
                {
                    rows.Add(new AudioDeviceOptionViewModel(selected, unavailableRowName, isAvailable: false));
                    canonical = selected;
                }
            }
            else
            {
                canonical = currentSelection;
            }

            canonicalSelection = canonical;
            return rows.AsReadOnly();
        }

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
