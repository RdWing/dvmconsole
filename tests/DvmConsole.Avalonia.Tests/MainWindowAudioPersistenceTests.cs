// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using dvmconsole;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for MainWindowViewModel's optional audio persistence
    /// composition. AudioSettingsViewModel remains persistence-neutral; the
    /// host view-model owns loading, mapping, saving, and failure isolation.
    /// </summary>
    public sealed class MainWindowAudioPersistenceTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-main-window-audio-persistence-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(Root);

            public string SettingsPath => Path.Combine(Root, "UserSettings.json");

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root))
                    {
                        Directory.Delete(Root, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // Best-effort cleanup; never mask the test result.
                }
            }
        }

        [Fact]
        public void FourArgumentConstructor_SeedsAudioStateFromPersistedSection()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsAudioSection
            {
                AudioInputDeviceKey = "mic-1",
                MasterOutputDeviceKey = "spk-1",
                AudioInputAgcEnabled = true
            });
            var vm = CreateViewModel(persistence);

            Assert.Equal(AudioDeviceId.FromKey("mic-1"), vm.AudioSettings!.SelectedInputId);
            Assert.Equal(AudioDeviceId.FromKey("spk-1"), vm.AudioSettings.SelectedOutputId);
            Assert.True(vm.AudioSettings.AgcEnabled);
        }

        [Fact]
        public void Commit_PersistsCurrentAudioStateAndPreservesUnrelatedFields()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }],
                  "WindowLayout": { "Width": 1200 }
                }
                """);
            var persistence = CreatePersistence(dir.SettingsPath);
            var vm = CreateViewModel(persistence);
            vm.AudioSettings!.SelectedInputId = AudioDeviceId.FromKey("mic-1");
            vm.AudioSettings.SelectedOutputId = AudioDeviceId.FromKey("spk-1");
            vm.AudioSettings.AgcEnabled = true;

            vm.AudioSettings.Commit();

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal("mic-1", (string)saved["AudioInputDeviceKey"]!);
            Assert.Equal("spk-1", (string)saved["MasterOutputDeviceKey"]!);
            Assert.True((bool)saved["AudioInputAgcEnabled"]!);
            Assert.Equal("system-1", (string)saved["FneSystems"]![0]!["Name"]!);
            Assert.Equal(62031, (int)saved["FneSystems"]![0]!["Port"]!);
            Assert.Equal(1200, (int)saved["WindowLayout"]!["Width"]!);
            Assert.Equal("Audio settings saved", vm.AudioSaveFeedback);
        }

        [Fact]
        public void ResourceAudioState_LoadsByStableKey_AndSavesExplicitThenInheritedOutput()
        {
            using var dir = new TempDir();
            var codeplug = MakeCodeplug();
            var resourceKey = ResourceIdentity.Build("Repeater 1", "31001");
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsAudioSection
            {
                MasterOutputDeviceKey = "spk-1",
                ChannelOutputDeviceKeys = new Dictionary<string, string>
                {
                    [resourceKey] = "spk-1",
                },
                ChannelVolumes = new Dictionary<string, double>
                {
                    [resourceKey] = 3.5,
                },
            });

            var vm = new MainWindowViewModel(
                codeplug.Systems,
                new FakeAudioDeviceCatalog(CreateInputs(), CreateOutputs()),
                null,
                persistence,
                null,
                codeplug);
            var slot = Assert.Single(vm.Channels);

            Assert.Equal(3.5, slot.Volume);
            Assert.Equal(AudioDeviceId.FromKey("spk-1"), vm.ResolveMonitorOutputDevice(resourceKey));
            Assert.Equal(
                AudioDeviceId.FromKey("spk-1"),
                vm.ResolveMonitorOutputDevice(resourceKey + "|slot:1"));
            Assert.Equal(3.5f, vm.ResolveMonitorVolume(resourceKey + "|slot:1"));
            Assert.False(slot.MonitorOutputDevice!.IsInheritMaster);
            Assert.Equal("spk-1", slot.MonitorOutputDevice.Id.Value);

            vm.ToggleSelectAllCurrentZone();
            Assert.True(vm.IsMonitorEnabled(resourceKey + "|slot:1"));

            vm.SetMonitorOutputDevice(slot, AudioDeviceId.FromKey("spk-1"));
            var savedExplicit = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal("spk-1", (string)savedExplicit["ChannelOutputDeviceKeys"]![resourceKey]!);
            Assert.Equal(0, (int)savedExplicit["ChannelOutputDevices"]![resourceKey]!);

            slot.MonitorOutputDevice = slot.MonitorOutputDevices[0];
            var savedInherited = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Null(savedInherited["ChannelOutputDeviceKeys"]![resourceKey]);
            Assert.Null(savedInherited["ChannelOutputDevices"]![resourceKey]);
            Assert.Equal(AudioDeviceId.FromKey("spk-1"), vm.ResolveMonitorOutputDevice(resourceKey));
        }

        [Fact]
        public void ResourceAudioState_LegacyOutputIndexLoadsWhenStableKeyIsAbsent()
        {
            using var dir = new TempDir();
            var codeplug = MakeCodeplug();
            var resourceKey = ResourceIdentity.Build("Repeater 1", "31001");
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsAudioSection
            {
                MasterOutputDeviceKey = "spk-1",
                ChannelOutputDevices = new Dictionary<string, int>
                {
                    [resourceKey] = 0,
                },
            });

            var vm = new MainWindowViewModel(
                codeplug.Systems,
                new FakeAudioDeviceCatalog(CreateInputs(), CreateOutputs()),
                null,
                persistence,
                null,
                codeplug);

            Assert.Equal(AudioDeviceId.FromKey("spk-1"), vm.ResolveMonitorOutputDevice(resourceKey));
            Assert.Equal("spk-1", vm.Channels[0].MonitorOutputDevice!.Id.Value);
        }

        [Fact]
        public void ResourceAudioState_PreservesExplicitSystemDefaultSeparateFromInheritMaster()
        {
            using var dir = new TempDir();
            var codeplug = MakeCodeplug();
            var resourceKey = ResourceIdentity.Build("Repeater 1", "31001");
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsAudioSection
            {
                MasterOutputDeviceKey = "spk-1",
                ChannelOutputDevices = new Dictionary<string, int>
                {
                    [resourceKey] = -1,
                },
                ChannelOutputDeviceKeys = new Dictionary<string, string>
                {
                    [resourceKey] = "windows-default",
                },
            });

            var vm = new MainWindowViewModel(
                codeplug.Systems,
                new FakeAudioDeviceCatalog(CreateInputs(), CreateOutputs()),
                null,
                persistence,
                null,
                codeplug);
            var slot = Assert.Single(vm.Channels);

            Assert.Equal(AudioDeviceId.Default, vm.ResolveMonitorOutputDevice(resourceKey));
            Assert.NotNull(slot.MonitorOutputDevice);
            Assert.False(slot.MonitorOutputDevice!.IsInheritMaster);
            Assert.Equal(AudioDeviceId.Default, slot.MonitorOutputDevice.Id);
            Assert.Equal("System Default Output", slot.MonitorOutputDevice.Name);

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal(
                "windows-default",
                (string)saved["ChannelOutputDeviceKeys"]![resourceKey]!);
            Assert.Equal(-1, (int)saved["ChannelOutputDevices"]![resourceKey]!);
        }

        [Fact]
        public void ResourceAudioState_StaleOutputFallsBackToAvailableMaster()
        {
            using var dir = new TempDir();
            var codeplug = MakeCodeplug();
            var resourceKey = ResourceIdentity.Build("Repeater 1", "31001");
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsAudioSection
            {
                MasterOutputDeviceKey = "spk-1",
                ChannelOutputDeviceKeys = new Dictionary<string, string>
                {
                    [resourceKey] = "removed-speaker",
                },
            });

            var vm = new MainWindowViewModel(
                codeplug.Systems,
                new FakeAudioDeviceCatalog(CreateInputs(), CreateOutputs()),
                null,
                persistence,
                null,
                codeplug);

            Assert.Equal(AudioDeviceId.FromKey("spk-1"), vm.ResolveMonitorOutputDevice(resourceKey));
            Assert.NotNull(vm.Channels[0].MonitorOutputDevice);
            Assert.False(vm.Channels[0].MonitorOutputDevice!.IsAvailable);
            Assert.Equal("removed-speaker", vm.Channels[0].MonitorOutputDevice!.Id.Value);
            Assert.Contains(
                vm.Channels[0].MonitorOutputDevices,
                option => option.Id.Value == "removed-speaker" && !option.IsAvailable);
        }

        [Fact]
        public void MalformedLoad_DoesNotThrowAndUsesAudioDefaults()
        {
            using var dir = new TempDir();
            File.WriteAllText(dir.SettingsPath, "{ not valid json");

            var vm = CreateViewModel(CreatePersistence(dir.SettingsPath));

            Assert.NotNull(vm.AudioSettings);
            Assert.Equal(AudioDeviceId.Default, vm.AudioSettings!.SelectedInputId);
            Assert.Equal(AudioDeviceId.Default, vm.AudioSettings.SelectedOutputId);
            Assert.False(vm.AudioSettings.AgcEnabled);
        }

        [Fact]
        public void MalformedSave_DoesNotCrashTheHostAndStillAcknowledgesCommit()
        {
            using var dir = new TempDir();
            const string malformed = "{ not valid json";
            File.WriteAllText(dir.SettingsPath, malformed);
            var vm = CreateViewModel(CreatePersistence(dir.SettingsPath));

            var exception = Record.Exception(() => vm.AudioSettings!.Commit());

            Assert.Null(exception);
            Assert.Equal("Audio settings saved", vm.AudioSaveFeedback);
            Assert.Equal(malformed, File.ReadAllText(dir.SettingsPath));
        }

        [Fact]
        public void NullPersistence_PreservesRequestOnlyAudioBehavior()
        {
            var vm = new MainWindowViewModel(
                null,
                new FakeAudioDeviceCatalog(CreateInputs(), CreateOutputs()),
                null,
                null);

            vm.AudioSettings!.Commit();

            Assert.Equal("Audio settings saved", vm.AudioSaveFeedback);
        }

        private static MainWindowViewModel CreateViewModel(AudioSettingsPersistence persistence)
            => new(
                null,
                new FakeAudioDeviceCatalog(CreateInputs(), CreateOutputs()),
                null,
                persistence);

        private static AudioSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));

        private static IReadOnlyList<AudioDeviceInfo> CreateInputs()
            => new[]
            {
                new AudioDeviceInfo(
                    AudioDeviceId.FromKey("mic-1"),
                    AudioDeviceDirection.Input,
                    "Microphone 1"),
            };

        private static IReadOnlyList<AudioDeviceInfo> CreateOutputs()
            => new[]
            {
                new AudioDeviceInfo(
                    AudioDeviceId.FromKey("spk-1"),
                    AudioDeviceDirection.Output,
                    "Speaker 1"),
            };

        private static Codeplug MakeCodeplug()
            => new()
            {
                Systems = new List<Codeplug.System>
                {
                    new Codeplug.System { Name = "Repeater 1", Rid = "1000001" },
                },
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone
                    {
                        Name = "Zone A",
                        Channels = new List<Codeplug.Channel>
                        {
                            new Codeplug.Channel
                            {
                                Name = "CH 1",
                                System = "Repeater 1",
                                Tgid = "31001",
                                Slot = 1,
                                Mode = "dmr",
                            },
                        },
                    },
                },
            };

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
