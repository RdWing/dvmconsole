// SPDX-License-Identifier: AGPL-3.0-only
/**
* Contract gate for the explicit-refresh Avalonia audio-settings
* view-model slice:
*
*   DvmConsole.Avalonia.ViewModels.AudioDeviceOptionViewModel
*   DvmConsole.Avalonia.ViewModels.AudioSettingsViewModel
*
* AudioDeviceOptionViewModel is a sealed, read-only row wrapping one
* selectable device: Id (AudioDeviceId), Name (string), IsAvailable (bool),
* built by a single (AudioDeviceId, string, bool) ctor and with no other
* public surface — no secrets, no native handles, no UI.
*
* AudioSettingsViewModel is a sealed INotifyPropertyChanged view-model
* constructed from an IAudioDeviceCatalog snapshot plus optional saved
* selection/AGC state. InputDevices / OutputDevices are read-only option
* lists whose first entry is always the available system-default row
* ("System Default Input" / "System Default Output"), followed by the
* catalog's devices in source order with case-insensitive id dedup and
* default-id entries excluded. Selection is the nullable AudioDeviceId?
* SelectedInputId / SelectedOutputId (defaulting to AudioDeviceId.Default)
* plus bool AgcEnabled, all with change-only PropertyChanged
* notifications. A saved id absent from its direction is appended after
* the real devices as exactly one IsAvailable=false row with a neutral
* name ("Saved input device unavailable; using system default until it
* returns" / "Saved output device unavailable; using system default until
* it returns") and the selection stays the saved id.
*
* Refresh() re-snapshots both lists, preserves selections
* case-insensitively (canonicalizing to the returned current catalog id
* when present), re-appends unavailable selected rows when absent,
* replaces both list snapshots, raises InputDevices/OutputDevices exactly
* once each (plus SelectedInputId/SelectedOutputId only when the canonical
* selected value actually changes), and never resets AgcEnabled. Commit()
* raises SaveRequested exactly once with the current selection (falling
* back to AudioDeviceId.Default) and AgcEnabled, with no persistence and
* no state mutation.
*
* This slice is explicit-refresh by design: there is no catalog event
* subscription, no DevicesChanged dependency, no IDisposable surface, and
* no persistence. The future UI/native layer calls Refresh() itself after
* marshaling concrete catalog changes.
*
* The tests are fully headless and pure managed: a mutable in-test fake
* implements IAudioDeviceCatalog; no Avalonia.Headless package, window,
* display, native call, Mac implementation, file, or secret is involved.
*
* This file is the executable contract for the managed view-model slice.
*/
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Contract gate for <c>AudioDeviceOptionViewModel</c> and
    /// <c>AudioSettingsViewModel</c>.
    /// </summary>
    public sealed class AudioSettingsViewModelTests
    {
        // ---- Fixtures ---------------------------------------------------------

        /// <summary>
        /// Mutable, headless <see cref="IAudioDeviceCatalog"/> fake: plain
        /// managed lists, default lookups by the
        /// <see cref="AudioDeviceId.IsDefault"/> marker, case-insensitive id
        /// resolution, completed disposal. No events and no native code —
        /// the slice under test must never depend on either.
        /// </summary>
        private sealed class FakeAudioDeviceCatalog : IAudioDeviceCatalog
        {
            private readonly List<AudioDeviceInfo> _inputs = new();
            private readonly List<AudioDeviceInfo> _outputs = new();

            public void AddInput(AudioDeviceInfo device) => _inputs.Add(device);

            public void AddOutput(AudioDeviceInfo device) => _outputs.Add(device);

            public void RemoveInput(string key)
                => _inputs.RemoveAll(d => string.Equals(d.Id.Value, key, StringComparison.OrdinalIgnoreCase));

            public void RemoveOutput(string key)
                => _outputs.RemoveAll(d => string.Equals(d.Id.Value, key, StringComparison.OrdinalIgnoreCase));

            public IReadOnlyList<AudioDeviceInfo> GetInputs() => _inputs.ToArray();

            public IReadOnlyList<AudioDeviceInfo> GetOutputs() => _outputs.ToArray();

            public AudioDeviceInfo? GetDefaultInput() => _inputs.FirstOrDefault(d => d.Id.IsDefault);

            public AudioDeviceInfo? GetDefaultOutput() => _outputs.FirstOrDefault(d => d.Id.IsDefault);

            public bool TryFind(AudioDeviceId id, out AudioDeviceInfo? device)
            {
                if (id.IsDefault)
                {
                    device = GetDefaultOutput() ?? GetDefaultInput();
                    return device is not null;
                }

                device = _inputs.Concat(_outputs).FirstOrDefault(d =>
                    string.Equals(d.Id.Value, id.Value, StringComparison.OrdinalIgnoreCase));
                return device is not null;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        /// <summary>
        /// Builds a non-default device identity.
        /// </summary>
        private static AudioDeviceInfo Device(string key, string name, AudioDeviceDirection direction)
            => new(AudioDeviceId.FromKey(key), direction, name);

        /// <summary>
        /// Builds a default-device identity.
        /// </summary>
        private static AudioDeviceInfo DefaultDevice(string name, AudioDeviceDirection direction)
            => new(AudioDeviceId.Default, direction, name);

        // ---- A. AudioDeviceOptionViewModel: row shape --------------------------

        /// <summary>
        /// Shape gate for the option row: sealed type, exactly three public
        /// read-only instance properties (Id / Name / IsAvailable) with the
        /// exact contract types, and a single (AudioDeviceId, string, bool)
        /// ctor. No secrets, native handles, or UI surface.
        /// </summary>
        [Fact]
        public void OptionRow_Shape_Sealed_ReadOnlyProps_ExactCtor_NoExtras()
        {
            var row = typeof(AudioDeviceOptionViewModel);

            Assert.True(row.IsSealed);

            var names = row.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "Id", "IsAvailable", "Name" }, names);

            Assert.Equal(typeof(AudioDeviceId), row.GetProperty(nameof(AudioDeviceOptionViewModel.Id))!.PropertyType);
            Assert.Equal(typeof(string), row.GetProperty(nameof(AudioDeviceOptionViewModel.Name))!.PropertyType);
            Assert.Equal(typeof(bool), row.GetProperty(nameof(AudioDeviceOptionViewModel.IsAvailable))!.PropertyType);

            Assert.False(row.GetProperty(nameof(AudioDeviceOptionViewModel.Id))!.CanWrite);
            Assert.False(row.GetProperty(nameof(AudioDeviceOptionViewModel.Name))!.CanWrite);
            Assert.False(row.GetProperty(nameof(AudioDeviceOptionViewModel.IsAvailable))!.CanWrite);

            var ctor = row.GetConstructors();
            Assert.Single(ctor);
            var parameters = ctor[0].GetParameters();
            Assert.Equal(3, parameters.Length);
            Assert.Equal(typeof(AudioDeviceId), parameters[0].ParameterType);
            Assert.Equal(typeof(string), parameters[1].ParameterType);
            Assert.Equal(typeof(bool), parameters[2].ParameterType);
        }

        /// <summary>
        /// The row projects the ctor arguments verbatim.
        /// </summary>
        [Fact]
        public void OptionRow_Ctor_ProjectsValuesVerbatim()
        {
            var id = AudioDeviceId.FromKey("usb-mic");
            var row = new AudioDeviceOptionViewModel(id, "USB Mic", true);

            Assert.Equal(id, row.Id);
            Assert.Equal("USB Mic", row.Name);
            Assert.True(row.IsAvailable);

            var unavailable = new AudioDeviceOptionViewModel(AudioDeviceId.Default, "System Default Input", false);
            Assert.Equal(AudioDeviceId.Default, unavailable.Id);
            Assert.Equal("System Default Input", unavailable.Name);
            Assert.False(unavailable.IsAvailable);
        }

        // ---- B. AudioSettingsViewModel: shape ----------------------------------

        /// <summary>
        /// Shape gate for the view-model: sealed, INotifyPropertyChanged,
        /// exactly five public instance properties of the exact contract
        /// types with the exact read/write access, the SaveRequested event
        /// with the exact payload delegate type, the PropertyChanged event,
        /// a single four-parameter ctor with the exact parameter types and
        /// defaults — and no IDisposable surface (no persistence, no native
        /// ownership, no catalog event subscription).
        /// </summary>
        [Fact]
        public void Settings_Shape_SealedObservable_ExactSurface_ExactCtor()
        {
            var vm = typeof(AudioSettingsViewModel);

            Assert.True(vm.IsSealed);
            Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(vm));
            Assert.False(typeof(IDisposable).IsAssignableFrom(vm));

            var names = vm.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                new[] { "AgcEnabled", "InputDevices", "OutputDevices", "SelectedInputId", "SelectedOutputId" },
                names);

            Assert.Equal(
                typeof(IReadOnlyList<AudioDeviceOptionViewModel>),
                vm.GetProperty(nameof(AudioSettingsViewModel.InputDevices))!.PropertyType);
            Assert.Equal(
                typeof(IReadOnlyList<AudioDeviceOptionViewModel>),
                vm.GetProperty(nameof(AudioSettingsViewModel.OutputDevices))!.PropertyType);
            Assert.Equal(
                typeof(AudioDeviceId?),
                vm.GetProperty(nameof(AudioSettingsViewModel.SelectedInputId))!.PropertyType);
            Assert.Equal(
                typeof(AudioDeviceId?),
                vm.GetProperty(nameof(AudioSettingsViewModel.SelectedOutputId))!.PropertyType);
            Assert.Equal(typeof(bool), vm.GetProperty(nameof(AudioSettingsViewModel.AgcEnabled))!.PropertyType);

            Assert.False(vm.GetProperty(nameof(AudioSettingsViewModel.InputDevices))!.CanWrite);
            Assert.False(vm.GetProperty(nameof(AudioSettingsViewModel.OutputDevices))!.CanWrite);
            Assert.True(vm.GetProperty(nameof(AudioSettingsViewModel.SelectedInputId))!.CanWrite);
            Assert.True(vm.GetProperty(nameof(AudioSettingsViewModel.SelectedOutputId))!.CanWrite);
            Assert.True(vm.GetProperty(nameof(AudioSettingsViewModel.AgcEnabled))!.CanWrite);

            Assert.Equal(
                typeof(Action<AudioDeviceId, AudioDeviceId, bool>),
                vm.GetEvent(nameof(AudioSettingsViewModel.SaveRequested))!.EventHandlerType);
            Assert.Equal(
                typeof(PropertyChangedEventHandler),
                vm.GetEvent("PropertyChanged")!.EventHandlerType);

            var ctor = vm.GetConstructors();
            Assert.Single(ctor);
            var parameters = ctor[0].GetParameters();
            Assert.Equal(4, parameters.Length);
            Assert.Equal(typeof(IAudioDeviceCatalog), parameters[0].ParameterType);
            Assert.Equal(typeof(AudioDeviceId?), parameters[1].ParameterType);
            Assert.Equal(typeof(AudioDeviceId?), parameters[2].ParameterType);
            Assert.Equal(typeof(bool), parameters[3].ParameterType);
            Assert.True(parameters[1].IsOptional);
            Assert.True(parameters[2].IsOptional);
            Assert.True(parameters[3].IsOptional);
        }

        /// <summary>
        /// A null catalog is rejected.
        /// </summary>
        [Fact]
        public void Settings_Ctor_NullCatalog_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() => new AudioSettingsViewModel(null!));
        }

        /// <summary>
        /// AgcEnabled defaults to false and honors the ctor flag.
        /// </summary>
        [Fact]
        public void Settings_Ctor_AgcEnabled_DefaultFalse_AndTrueWhenPassed()
        {
            var defaulted = new AudioSettingsViewModel(new FakeAudioDeviceCatalog());
            Assert.False(defaulted.AgcEnabled);

            var enabled = new AudioSettingsViewModel(new FakeAudioDeviceCatalog(), agcEnabled: true);
            Assert.True(enabled.AgcEnabled);
        }

        // ---- C. Initial / Refresh device rows -----------------------------------

        /// <summary>
        /// An empty catalog still yields exactly one available system-default
        /// row per direction, and selections default to AudioDeviceId.Default.
        /// </summary>
        [Fact]
        public void Initial_EmptyCatalog_DefaultRowsAndSelections()
        {
            var vm = new AudioSettingsViewModel(new FakeAudioDeviceCatalog());

            var input = Assert.Single(vm.InputDevices);
            Assert.Equal(AudioDeviceId.Default, input.Id);
            Assert.Equal("System Default Input", input.Name);
            Assert.True(input.IsAvailable);

            var output = Assert.Single(vm.OutputDevices);
            Assert.Equal(AudioDeviceId.Default, output.Id);
            Assert.Equal("System Default Output", output.Name);
            Assert.True(output.IsAvailable);

            Assert.Equal(AudioDeviceId.Default, vm.SelectedInputId);
            Assert.Equal(AudioDeviceId.Default, vm.SelectedOutputId);
        }

        /// <summary>
        /// Input rows start with the available system-default row, then each
        /// catalog input in source order; ids duplicate case-insensitively
        /// are dropped (first occurrence wins) and default-id entries are
        /// excluded entirely.
        /// </summary>
        [Fact]
        public void Initial_InputRows_DefaultFirst_SourceOrder_DedupCaseInsensitive_ExcludesDefaultId()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));
            catalog.AddInput(Device("LINE-IN", "Line In", AudioDeviceDirection.Input));
            catalog.AddInput(Device("USB-MIC", "USB Mic Duplicate", AudioDeviceDirection.Input));
            catalog.AddInput(DefaultDevice("Fake Default Marker", AudioDeviceDirection.Input));
            catalog.AddInput(Device("line-in", "Line In Duplicate", AudioDeviceDirection.Input));
            catalog.AddInput(Device("bluetooth", "BT Headset", AudioDeviceDirection.Input));

            var vm = new AudioSettingsViewModel(catalog);

            Assert.Equal(4, vm.InputDevices.Count);

            Assert.Equal(AudioDeviceId.Default, vm.InputDevices[0].Id);
            Assert.Equal("System Default Input", vm.InputDevices[0].Name);

            Assert.Equal("usb-mic", vm.InputDevices[1].Id.Value);
            Assert.Equal("USB Mic", vm.InputDevices[1].Name);
            Assert.Equal("LINE-IN", vm.InputDevices[2].Id.Value);
            Assert.Equal("Line In", vm.InputDevices[2].Name);
            Assert.Equal("bluetooth", vm.InputDevices[3].Id.Value);
            Assert.Equal("BT Headset", vm.InputDevices[3].Name);

            Assert.All(vm.InputDevices, r => Assert.True(r.IsAvailable));
        }

        /// <summary>
        /// Output rows mirror the input contract: system-default row first,
        /// then catalog outputs in source order with case-insensitive dedup
        /// and default-id exclusion; each row name equals the catalog name.
        /// </summary>
        [Fact]
        public void Initial_OutputRows_DefaultFirst_SourceOrder_DedupCaseInsensitive_ExcludesDefaultId()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddOutput(Device("speaker", "Speaker", AudioDeviceDirection.Output));
            catalog.AddOutput(Device("SPEAKER", "Speaker Duplicate", AudioDeviceDirection.Output));
            catalog.AddOutput(DefaultDevice("Fake Default Marker", AudioDeviceDirection.Output));
            catalog.AddOutput(Device("hdmi", "HDMI Audio", AudioDeviceDirection.Output));

            var vm = new AudioSettingsViewModel(catalog);

            Assert.Equal(3, vm.OutputDevices.Count);

            Assert.Equal(AudioDeviceId.Default, vm.OutputDevices[0].Id);
            Assert.Equal("System Default Output", vm.OutputDevices[0].Name);

            Assert.Equal("speaker", vm.OutputDevices[1].Id.Value);
            Assert.Equal("Speaker", vm.OutputDevices[1].Name);
            Assert.Equal("hdmi", vm.OutputDevices[2].Id.Value);
            Assert.Equal("HDMI Audio", vm.OutputDevices[2].Name);

            Assert.All(vm.OutputDevices, r => Assert.True(r.IsAvailable));
        }

        /// <summary>
        /// A saved input id matching an input device case-insensitively
        /// selects the canonical current catalog id.
        /// </summary>
        [Fact]
        public void Initial_SavedExistingInput_SelectsCanonicalCatalogId()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));

            var vm = new AudioSettingsViewModel(catalog, savedInputId: AudioDeviceId.FromKey("USB-MIC"));

            Assert.Equal(AudioDeviceId.FromKey("usb-mic"), vm.SelectedInputId);
            Assert.Equal(AudioDeviceId.Default, vm.SelectedOutputId);
        }

        /// <summary>
        /// A saved output id matching an output device case-insensitively
        /// selects the canonical current catalog id.
        /// </summary>
        [Fact]
        public void Initial_SavedExistingOutput_SelectsCanonicalCatalogId()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddOutput(Device("hdmi-out", "HDMI Out", AudioDeviceDirection.Output));

            var vm = new AudioSettingsViewModel(catalog, savedOutputId: AudioDeviceId.FromKey("HDMI-OUT"));

            Assert.Equal(AudioDeviceId.FromKey("hdmi-out"), vm.SelectedOutputId);
            Assert.Equal(AudioDeviceId.Default, vm.SelectedInputId);
        }

        /// <summary>
        /// Matching is per direction: a saved input id that exists only among
        /// outputs is appended to the input list as one IsAvailable=false row
        /// with the exact neutral name, and the selection stays the saved id.
        /// The output list is untouched.
        /// </summary>
        [Fact]
        public void Initial_SavedInputPresentOnlyInOutputs_UnavailableRow_NeutralName_SelectedStaysSaved()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddOutput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Output));

            var vm = new AudioSettingsViewModel(catalog, savedInputId: AudioDeviceId.FromKey("usb-mic"));

            Assert.Equal(2, vm.InputDevices.Count);
            Assert.True(vm.InputDevices[0].IsAvailable);
            Assert.Equal(AudioDeviceId.Default, vm.InputDevices[0].Id);
            Assert.False(vm.InputDevices[1].IsAvailable);
            Assert.Equal(AudioDeviceId.FromKey("usb-mic"), vm.InputDevices[1].Id);
            Assert.Equal(
                "Saved input device unavailable; using system default until it returns",
                vm.InputDevices[1].Name);
            Assert.Equal(AudioDeviceId.FromKey("usb-mic"), vm.SelectedInputId);

            Assert.Equal(2, vm.OutputDevices.Count);
            Assert.True(vm.OutputDevices[1].IsAvailable);
            Assert.Equal("USB Mic", vm.OutputDevices[1].Name);
            Assert.Equal(AudioDeviceId.Default, vm.SelectedOutputId);
        }

        /// <summary>
        /// Saved ids absent from their direction are appended after the real
        /// devices as exactly one IsAvailable=false row each, with the exact
        /// neutral names (no Windows labels or sentinels), and the selections
        /// stay the saved ids. Positional ctor arguments lock the parameter
        /// order too.
        /// </summary>
        [Fact]
        public void Initial_SavedMissing_AppendedUnavailableRows_NeutralNames_SelectedStaysSaved()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));

            var vm = new AudioSettingsViewModel(
                catalog,
                AudioDeviceId.FromKey("gone-input"),
                AudioDeviceId.FromKey("gone-output"));

            Assert.Equal(3, vm.InputDevices.Count);
            Assert.True(vm.InputDevices[0].IsAvailable);
            Assert.True(vm.InputDevices[1].IsAvailable);
            Assert.False(vm.InputDevices[2].IsAvailable);
            Assert.Equal(AudioDeviceId.FromKey("gone-input"), vm.InputDevices[2].Id);
            Assert.Equal(
                "Saved input device unavailable; using system default until it returns",
                vm.InputDevices[2].Name);
            Assert.Equal(AudioDeviceId.FromKey("gone-input"), vm.SelectedInputId);

            Assert.Equal(2, vm.OutputDevices.Count);
            Assert.False(vm.OutputDevices[1].IsAvailable);
            Assert.Equal(AudioDeviceId.FromKey("gone-output"), vm.OutputDevices[1].Id);
            Assert.Equal(
                "Saved output device unavailable; using system default until it returns",
                vm.OutputDevices[1].Name);
            Assert.Equal(AudioDeviceId.FromKey("gone-output"), vm.SelectedOutputId);
        }

        /// <summary>
        /// An explicitly saved default id never produces an unavailable row.
        /// </summary>
        [Fact]
        public void Initial_SavedDefaultId_NoUnavailableRow()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));

            var vm = new AudioSettingsViewModel(catalog, savedInputId: AudioDeviceId.Default);

            Assert.Equal(2, vm.InputDevices.Count);
            Assert.Equal(AudioDeviceId.Default, vm.InputDevices[0].Id);
            Assert.Equal("usb-mic", vm.InputDevices[1].Id.Value);
            Assert.Equal(AudioDeviceId.Default, vm.SelectedInputId);
        }

        /// <summary>
        /// The exposed lists are read-only surfaces: downcast mutation is
        /// rejected and can never affect the view-model's rows.
        /// </summary>
        [Fact]
        public void Initial_ListsAreReadOnly_DowncastMutationCannotAffectVm()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));
            var vm = new AudioSettingsViewModel(catalog);

            var mutableInputs = (IList<AudioDeviceOptionViewModel>)vm.InputDevices;
            var mutableOutputs = (IList<AudioDeviceOptionViewModel>)vm.OutputDevices;

            Assert.Throws<NotSupportedException>(() =>
                mutableInputs.Add(new AudioDeviceOptionViewModel(AudioDeviceId.FromKey("x"), "X", true)));
            Assert.Throws<NotSupportedException>(() => mutableInputs.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => mutableOutputs.Add(new AudioDeviceOptionViewModel(AudioDeviceId.FromKey("x"), "X", true)));
            Assert.Throws<NotSupportedException>(() => mutableOutputs.RemoveAt(0));

            var first = vm.InputDevices[0];
            try
            {
                mutableInputs[0] = new AudioDeviceOptionViewModel(AudioDeviceId.FromKey("x"), "X", true);
            }
            catch (NotSupportedException)
            {
                // A fully read-only wrapper rejects indexer mutation too.
            }

            Assert.Same(first, vm.InputDevices[0]);
            Assert.Equal(2, vm.InputDevices.Count);
            Assert.Single(vm.OutputDevices);
        }

        // ---- D. Selection, AGC, Refresh -----------------------------------------

        /// <summary>
        /// Setting SelectedInputId raises exactly SelectedInputId, only when
        /// the value actually changes; null transitions count as changes.
        /// </summary>
        [Fact]
        public void Select_Input_ChangeOnlyNotifications()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));
            catalog.AddInput(Device("line-in", "Line In", AudioDeviceDirection.Input));
            var vm = new AudioSettingsViewModel(catalog);
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.SelectedInputId = AudioDeviceId.FromKey("line-in");

            Assert.Equal(new List<string?> { "SelectedInputId" }, raised);
            Assert.Equal(AudioDeviceId.FromKey("line-in"), vm.SelectedInputId);

            raised.Clear();
            vm.SelectedInputId = null;
            Assert.Equal(new List<string?> { "SelectedInputId" }, raised);
            Assert.Null(vm.SelectedInputId);

            raised.Clear();
            vm.SelectedInputId = null;
            Assert.Empty(raised);
        }

        /// <summary>
        /// Setting SelectedOutputId raises exactly SelectedOutputId, only
        /// when the value actually changes.
        /// </summary>
        [Fact]
        public void Select_Output_ChangeOnlyNotifications()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddOutput(Device("speaker", "Speaker", AudioDeviceDirection.Output));
            var vm = new AudioSettingsViewModel(catalog);
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.SelectedOutputId = AudioDeviceId.FromKey("speaker");

            Assert.Equal(new List<string?> { "SelectedOutputId" }, raised);
            Assert.Equal(AudioDeviceId.FromKey("speaker"), vm.SelectedOutputId);

            raised.Clear();
            vm.SelectedOutputId = AudioDeviceId.FromKey("speaker");
            Assert.Empty(raised);
        }

        /// <summary>
        /// Same-value assignments to the selection properties raise nothing.
        /// </summary>
        [Fact]
        public void Select_SameValueAssignments_RaiseNothing()
        {
            var vm = new AudioSettingsViewModel(new FakeAudioDeviceCatalog());
            vm.SelectedInputId = AudioDeviceId.FromKey("usb-mic");
            vm.SelectedOutputId = AudioDeviceId.FromKey("speaker");
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.SelectedInputId = AudioDeviceId.FromKey("usb-mic");
            vm.SelectedOutputId = AudioDeviceId.FromKey("speaker");

            Assert.Empty(raised);
        }

        /// <summary>
        /// AgcEnabled raises exactly AgcEnabled, only on actual changes.
        /// </summary>
        [Fact]
        public void Agc_ChangeOnlyNotifications()
        {
            var vm = new AudioSettingsViewModel(new FakeAudioDeviceCatalog());
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.AgcEnabled = true;
            Assert.Equal(new List<string?> { "AgcEnabled" }, raised);
            Assert.True(vm.AgcEnabled);

            raised.Clear();
            vm.AgcEnabled = true;
            Assert.Empty(raised);

            raised.Clear();
            vm.AgcEnabled = false;
            Assert.Equal(new List<string?> { "AgcEnabled" }, raised);
            Assert.False(vm.AgcEnabled);
        }

        /// <summary>
        /// Refresh over an unchanged catalog raises InputDevices and
        /// OutputDevices exactly once each, nothing else — selections and
        /// AGC are preserved without notifications.
        /// </summary>
        [Fact]
        public void Refresh_Unchanged_NotifiesEachListExactlyOnce_SelectionAndAgcSilent()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));
            catalog.AddOutput(Device("speaker", "Speaker", AudioDeviceDirection.Output));
            var vm = new AudioSettingsViewModel(catalog, agcEnabled: true);
            vm.SelectedInputId = AudioDeviceId.FromKey("usb-mic");
            vm.SelectedOutputId = AudioDeviceId.FromKey("speaker");
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.Refresh();

            raised.Sort(StringComparer.Ordinal);
            Assert.Equal(new List<string?> { "InputDevices", "OutputDevices" }, raised);
            Assert.Equal(AudioDeviceId.FromKey("usb-mic"), vm.SelectedInputId);
            Assert.Equal(AudioDeviceId.FromKey("speaker"), vm.SelectedOutputId);
            Assert.True(vm.AgcEnabled);
        }

        /// <summary>
        /// Refresh preserves a selection case-insensitively and canonicalizes
        /// it to the returned current catalog id, notifying SelectedInputId
        /// only because the canonical value actually changed; no unavailable
        /// duplicate row is appended.
        /// </summary>
        [Fact]
        public void Refresh_CanonicalizesCaseInsensitiveSelectedId_NotifiesSelected()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));
            var vm = new AudioSettingsViewModel(catalog, savedInputId: AudioDeviceId.FromKey("USB-MIC"));
            Assert.Equal(AudioDeviceId.FromKey("usb-mic"), vm.SelectedInputId);

            catalog.RemoveInput("usb-mic");
            catalog.AddInput(Device("USB-MIC", "USB Mic", AudioDeviceDirection.Input));

            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.Refresh();

            raised.Sort(StringComparer.Ordinal);
            Assert.Equal(new List<string?> { "InputDevices", "OutputDevices", "SelectedInputId" }, raised);
            Assert.Equal(AudioDeviceId.FromKey("USB-MIC"), vm.SelectedInputId);
            Assert.Equal(2, vm.InputDevices.Count);
            Assert.True(vm.InputDevices[1].IsAvailable);
            Assert.Equal("USB Mic", vm.InputDevices[1].Name);
        }

        /// <summary>
        /// When the selected device disappears, Refresh re-appends exactly
        /// one IsAvailable=false row with the neutral name, keeps the
        /// selection, and does not raise a selection notification.
        /// </summary>
        [Fact]
        public void Refresh_DeviceGone_ReappendsUnavailableRow_SelectedPreserved()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));
            var vm = new AudioSettingsViewModel(catalog, savedInputId: AudioDeviceId.FromKey("usb-mic"));

            catalog.RemoveInput("usb-mic");

            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.Refresh();

            raised.Sort(StringComparer.Ordinal);
            Assert.Equal(new List<string?> { "InputDevices", "OutputDevices" }, raised);
            Assert.Equal(2, vm.InputDevices.Count);
            Assert.False(vm.InputDevices[1].IsAvailable);
            Assert.Equal(AudioDeviceId.FromKey("usb-mic"), vm.InputDevices[1].Id);
            Assert.Equal(
                "Saved input device unavailable; using system default until it returns",
                vm.InputDevices[1].Name);
            Assert.Equal(AudioDeviceId.FromKey("usb-mic"), vm.SelectedInputId);
        }

        /// <summary>
        /// When a previously unavailable saved device returns (under a
        /// different id casing), Refresh drops the unavailable row, selects
        /// the canonical returned id, and notifies SelectedInputId because
        /// the canonical value changed.
        /// </summary>
        [Fact]
        public void Refresh_DeviceReturns_DropsUnavailableRow_Canonicalizes()
        {
            var catalog = new FakeAudioDeviceCatalog();
            var vm = new AudioSettingsViewModel(catalog, savedInputId: AudioDeviceId.FromKey("usb-mic"));
            Assert.False(vm.InputDevices[1].IsAvailable);

            catalog.AddInput(Device("USB-MIC", "USB Mic", AudioDeviceDirection.Input));

            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.Refresh();

            raised.Sort(StringComparer.Ordinal);
            Assert.Equal(new List<string?> { "InputDevices", "OutputDevices", "SelectedInputId" }, raised);
            Assert.Equal(2, vm.InputDevices.Count);
            Assert.True(vm.InputDevices[1].IsAvailable);
            Assert.Equal(AudioDeviceId.FromKey("USB-MIC"), vm.InputDevices[1].Id);
            Assert.Equal("USB Mic", vm.InputDevices[1].Name);
            Assert.Equal(AudioDeviceId.FromKey("USB-MIC"), vm.SelectedInputId);
        }

        /// <summary>
        /// A still-missing saved id keeps its unavailable row across Refresh,
        /// with no selection notification.
        /// </summary>
        [Fact]
        public void Refresh_StillMissingSavedId_RebuildsUnavailableRow_NoSelectionNotification()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));
            var vm = new AudioSettingsViewModel(catalog, savedInputId: AudioDeviceId.FromKey("gone"));
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.Refresh();

            raised.Sort(StringComparer.Ordinal);
            Assert.Equal(new List<string?> { "InputDevices", "OutputDevices" }, raised);
            Assert.Equal(3, vm.InputDevices.Count);
            Assert.False(vm.InputDevices[2].IsAvailable);
            Assert.Equal(AudioDeviceId.FromKey("gone"), vm.InputDevices[2].Id);
            Assert.Equal(AudioDeviceId.FromKey("gone"), vm.SelectedInputId);
        }

        /// <summary>
        /// Refresh replaces both list snapshots with fresh catalog state
        /// (new instances, new rows) and never resets AgcEnabled.
        /// </summary>
        [Fact]
        public void Refresh_ReplacesListSnapshots_WithFreshCatalogState()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("old-mic", "Old Mic", AudioDeviceDirection.Input));
            var vm = new AudioSettingsViewModel(catalog, agcEnabled: true);
            var inputsBefore = vm.InputDevices;
            var outputsBefore = vm.OutputDevices;

            catalog.RemoveInput("old-mic");
            catalog.AddInput(Device("new-mic", "New Mic", AudioDeviceDirection.Input));
            catalog.AddOutput(Device("speaker", "Speaker", AudioDeviceDirection.Output));

            vm.Refresh();

            Assert.NotSame(inputsBefore, vm.InputDevices);
            Assert.NotSame(outputsBefore, vm.OutputDevices);

            Assert.Equal(2, vm.InputDevices.Count);
            Assert.Equal(AudioDeviceId.Default, vm.InputDevices[0].Id);
            Assert.Equal("new-mic", vm.InputDevices[1].Id.Value);
            Assert.Equal("New Mic", vm.InputDevices[1].Name);

            Assert.Equal(2, vm.OutputDevices.Count);
            Assert.Equal("speaker", vm.OutputDevices[1].Id.Value);
            Assert.Equal("Speaker", vm.OutputDevices[1].Name);

            Assert.True(vm.AgcEnabled);
            Assert.Equal(AudioDeviceId.Default, vm.SelectedInputId);
        }

        // ---- E. Commit -----------------------------------------------------------

        /// <summary>
        /// Commit raises SaveRequested exactly once per call with the exact
        /// payload: the current selections and AgcEnabled.
        /// </summary>
        [Fact]
        public void Commit_RaisesSaveRequestedExactlyOnce_WithExactPayload()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));
            catalog.AddOutput(Device("speaker", "Speaker", AudioDeviceDirection.Output));
            var vm = new AudioSettingsViewModel(catalog);
            vm.SelectedInputId = AudioDeviceId.FromKey("usb-mic");
            vm.SelectedOutputId = AudioDeviceId.FromKey("speaker");
            vm.AgcEnabled = true;

            var calls = new List<(AudioDeviceId Input, AudioDeviceId Output, bool Agc)>();
            vm.SaveRequested += (input, output, agc) => calls.Add((input, output, agc));

            vm.Commit();

            var call = Assert.Single(calls);
            Assert.Equal(AudioDeviceId.FromKey("usb-mic"), call.Input);
            Assert.Equal(AudioDeviceId.FromKey("speaker"), call.Output);
            Assert.True(call.Agc);

            vm.Commit();
            Assert.Equal(2, calls.Count);
            Assert.Equal(AudioDeviceId.FromKey("usb-mic"), calls[1].Input);
            Assert.Equal(AudioDeviceId.FromKey("speaker"), calls[1].Output);
            Assert.True(calls[1].Agc);
        }

        /// <summary>
        /// Commit falls back to AudioDeviceId.Default for selections that are
        /// somehow null, so the payload is always non-null.
        /// </summary>
        [Fact]
        public void Commit_NullSelections_FallbackToDefaultInPayload()
        {
            var vm = new AudioSettingsViewModel(new FakeAudioDeviceCatalog());
            vm.SelectedInputId = null;
            vm.SelectedOutputId = null;

            var calls = new List<(AudioDeviceId Input, AudioDeviceId Output, bool Agc)>();
            vm.SaveRequested += (input, output, agc) => calls.Add((input, output, agc));

            vm.Commit();

            var call = Assert.Single(calls);
            Assert.Equal(AudioDeviceId.Default, call.Input);
            Assert.Equal(AudioDeviceId.Default, call.Output);
            Assert.False(call.Agc);
        }

        /// <summary>
        /// Commit performs no automatic state mutation: same list instances,
        /// same selections, same AGC, and no PropertyChanged notifications.
        /// </summary>
        [Fact]
        public void Commit_NoStateMutation_NoNotifications()
        {
            var catalog = new FakeAudioDeviceCatalog();
            catalog.AddInput(Device("usb-mic", "USB Mic", AudioDeviceDirection.Input));
            var vm = new AudioSettingsViewModel(catalog);
            vm.SelectedInputId = AudioDeviceId.FromKey("usb-mic");
            vm.AgcEnabled = true;

            var inputsBefore = vm.InputDevices;
            var outputsBefore = vm.OutputDevices;
            var selectedInputBefore = vm.SelectedInputId;
            var selectedOutputBefore = vm.SelectedOutputId;
            var agcBefore = vm.AgcEnabled;

            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var commits = 0;
            vm.SaveRequested += (_, _, _) => commits++;

            vm.Commit();

            Assert.Equal(1, commits);
            Assert.Empty(raised);
            Assert.Same(inputsBefore, vm.InputDevices);
            Assert.Same(outputsBefore, vm.OutputDevices);
            Assert.Equal(selectedInputBefore, vm.SelectedInputId);
            Assert.Equal(selectedOutputBefore, vm.SelectedOutputId);
            Assert.Equal(agcBefore, vm.AgcEnabled);
        }
    }
}
