// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for the shell-visible acknowledgement raised after
    /// the dashboard forwards an audio-settings Commit request. The
    /// acknowledgement belongs to MainWindowViewModel; AudioSettingsViewModel
    /// remains request-only and state/persistence neutral.
    /// </summary>
    public sealed class MainWindowAudioSaveFeedbackTests
    {
        [Fact]
        public void InitialState_IsEmpty_AndPropertyIsPublicReadOnly()
        {
            var property = typeof(MainWindowViewModel).GetProperty(
                nameof(MainWindowViewModel.AudioSaveFeedback));

            Assert.NotNull(property);
            Assert.Equal(typeof(string), property!.PropertyType);
            Assert.False(property.CanWrite);

            var vm = new MainWindowViewModel();

            Assert.Equal(string.Empty, vm.AudioSaveFeedback);
        }

        [Fact]
        public void Commit_WithAudioSettings_SetsExactFeedback_AndRaisesOnce()
        {
            var vm = CreateViewModel();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.AudioSettings!.Commit();

            Assert.Equal("Audio settings saved", vm.AudioSaveFeedback);
            Assert.Equal(new[] { nameof(MainWindowViewModel.AudioSaveFeedback) }, raised);
        }

        [Fact]
        public void RepeatedCommit_WhenAlreadyAcknowledged_IsChangeOnly()
        {
            var vm = CreateViewModel();
            vm.AudioSettings!.Commit();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.AudioSettings.Commit();

            Assert.Equal("Audio settings saved", vm.AudioSaveFeedback);
            Assert.Empty(raised);
        }

        [Fact]
        public void ChangingSelectedInput_ClearsAcknowledgement()
        {
            var vm = CreateViewModel();
            vm.AudioSettings!.Commit();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.AudioSettings.SelectedInputId = AudioDeviceId.FromKey("mic-1");

            Assert.Equal(string.Empty, vm.AudioSaveFeedback);
            Assert.Equal(new[] { nameof(MainWindowViewModel.AudioSaveFeedback) }, raised);
        }

        [Fact]
        public void ChangingSelectedOutput_ClearsAcknowledgement()
        {
            var vm = CreateViewModel();
            vm.AudioSettings!.Commit();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.AudioSettings.SelectedOutputId = AudioDeviceId.FromKey("spk-1");

            Assert.Equal(string.Empty, vm.AudioSaveFeedback);
            Assert.Equal(new[] { nameof(MainWindowViewModel.AudioSaveFeedback) }, raised);
        }

        [Fact]
        public void ChangingAgc_ClearsAcknowledgement()
        {
            var vm = CreateViewModel();
            vm.AudioSettings!.Commit();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.AudioSettings.AgcEnabled = true;

            Assert.Equal(string.Empty, vm.AudioSaveFeedback);
            Assert.Equal(new[] { nameof(MainWindowViewModel.AudioSaveFeedback) }, raised);
        }

        [Fact]
        public void SameValueAudioChanges_DoNotClearOrNotify()
        {
            var vm = CreateViewModel();
            vm.AudioSettings!.Commit();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.AudioSettings.SelectedInputId = vm.AudioSettings.SelectedInputId;
            vm.AudioSettings.SelectedOutputId = vm.AudioSettings.SelectedOutputId;
            vm.AudioSettings.AgcEnabled = vm.AudioSettings.AgcEnabled;

            Assert.Equal("Audio settings saved", vm.AudioSaveFeedback);
            Assert.Empty(raised);
        }

        [Fact]
        public void NullCatalog_DoesNotAcknowledgeOrSubscribe()
        {
            var vm = new MainWindowViewModel(null, null);
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            Assert.Null(vm.AudioSettings);
            Assert.Equal(string.Empty, vm.AudioSaveFeedback);
            Assert.Empty(raised);
        }

        private static MainWindowViewModel CreateViewModel()
            => new MainWindowViewModel(
                null,
                new FakeAudioDeviceCatalog(
                    new[]
                    {
                        new AudioDeviceInfo(
                            AudioDeviceId.FromKey("mic-1"),
                            AudioDeviceDirection.Input,
                            "Built-in Microphone"),
                    },
                    new[]
                    {
                        new AudioDeviceInfo(
                            AudioDeviceId.FromKey("spk-1"),
                            AudioDeviceDirection.Output,
                            "Built-in Speakers"),
                    }));

        private sealed class FakeAudioDeviceCatalog : IAudioDeviceCatalog
        {
            private readonly IReadOnlyList<AudioDeviceInfo> inputs;
            private readonly IReadOnlyList<AudioDeviceInfo> outputs;

            public FakeAudioDeviceCatalog(
                IReadOnlyList<AudioDeviceInfo> inputs,
                IReadOnlyList<AudioDeviceInfo> outputs)
            {
                this.inputs = inputs;
                this.outputs = outputs;
            }

            public IReadOnlyList<AudioDeviceInfo> GetInputs() => inputs;

            public IReadOnlyList<AudioDeviceInfo> GetOutputs() => outputs;

            public AudioDeviceInfo? GetDefaultInput() => null;

            public AudioDeviceInfo? GetDefaultOutput() => null;

            public bool TryFind(AudioDeviceId id, out AudioDeviceInfo? device)
            {
                device = null;
                return false;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
