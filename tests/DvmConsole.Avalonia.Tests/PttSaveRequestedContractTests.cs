// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Hotkeys;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class PttSaveRequestedContractTests
    {
        [Fact]
        public void SaveRequested_IsEffectiveChangeOnlyAndCarriesCurrentState()
        {
            var ptt = CreatePtt();
            var requests = new List<(HotkeyGesture? Gesture, bool Toggle, bool AllChannels)>();
            ptt.SaveRequested += (gesture, toggle, allChannels) =>
                requests.Add((gesture, toggle, allChannels));

            ptt.ToggleMode = true;
            ptt.ToggleMode = true;
            ptt.AllChannels = true;
            ptt.SetHotkey(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.None));
            ptt.SetHotkey(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.None));
            ptt.ClearHotkey();
            ptt.ClearHotkey();

            Assert.Collection(
                requests,
                request =>
                {
                    Assert.Null(request.Gesture);
                    Assert.True(request.Toggle);
                    Assert.False(request.AllChannels);
                },
                request =>
                {
                    Assert.Null(request.Gesture);
                    Assert.True(request.Toggle);
                    Assert.True(request.AllChannels);
                },
                request =>
                {
                    Assert.Equal(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.None), request.Gesture);
                    Assert.True(request.Toggle);
                    Assert.True(request.AllChannels);
                },
                request =>
                {
                    Assert.Null(request.Gesture);
                    Assert.True(request.Toggle);
                    Assert.True(request.AllChannels);
                });
        }

        [Fact]
        public void Composition_PersistsModeScopeAndSupportedReverseEncoding()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsPttSection
            {
                GlobalPTTShortcut = 0x20041
            });

            var vm = CreateViewModel(persistence);
            vm.Ptt!.ToggleMode = true;
            vm.Ptt.AllChannels = true;
            vm.Ptt.SetHotkey(new HotkeyGesture(HotkeyKey.F2, HotkeyModifiers.Shift));

            Assert.True(persistence.TryLoad(out var stored));
            Assert.Equal(0x10071, stored.GlobalPTTShortcut);
            Assert.True(stored.TogglePTTMode);
            Assert.True(stored.GlobalPTTKeysAllChannels);
        }

        [Fact]
        public void Composition_ClearHotkey_PersistsZeroShortcut()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsPttSection { GlobalPTTShortcut = 0x70 });

            var vm = CreateViewModel(persistence);
            vm.Ptt!.ClearHotkey();

            Assert.True(persistence.TryLoad(out var stored));
            Assert.Equal(0, stored.GlobalPTTShortcut);
        }

        [Fact]
        public void Composition_UnsupportedGesture_PreservesPriorShortcut()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsPttSection { GlobalPTTShortcut = 0x20041 });

            var vm = CreateViewModel(persistence);
            var exception = Record.Exception(() =>
                vm.Ptt!.SetHotkey(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta)));

            Assert.Null(exception);
            Assert.True(persistence.TryLoad(out var stored));
            Assert.Equal(0x20041, stored.GlobalPTTShortcut);
        }

        [Fact]
        public void Composition_SaveFailure_IsolatedWithoutRetry()
        {
            using var dir = new TempDir();
            File.WriteAllText(dir.SettingsPath, "{ not valid json");
            var vm = CreateViewModel(CreatePersistence(dir.SettingsPath));

            var exception = Record.Exception(() =>
                vm.Ptt!.SetHotkey(new HotkeyGesture(HotkeyKey.F3, HotkeyModifiers.None)));

            Assert.Null(exception);
            Assert.Equal("{ not valid json", File.ReadAllText(dir.SettingsPath));
        }

        private static PttCapabilityViewModel CreatePtt()
            => new(
                new RecordingHotkeyService(),
                () => null,
                () => Array.Empty<ChannelSlotViewModel>());

        private static MainWindowViewModel CreateViewModel(PttSettingsPersistence persistence)
            => new(
                null,
                null,
                new RecordingHotkeyService(),
                null,
                null,
                null,
                null,
                null,
                persistence);

        private static PttSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));

        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-ptt-save-" + Guid.NewGuid().ToString("N"));

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
                }
            }
        }

        private sealed class RecordingHotkeyService : IGlobalHotkeyService
        {
            public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

            public HotkeyCapability GetCapability(HotkeyGesture gesture)
                => HotkeyCapability.Available;

            public Task<HotkeyRegistrationResult> RegisterAsync(
                HotkeyGesture gesture,
                CancellationToken cancellationToken)
                => Task.FromResult(new HotkeyRegistrationResult(
                    HotkeyRegistrationStatus.Registered,
                    gesture));

            public Task UnregisterAsync(
                HotkeyGesture gesture,
                CancellationToken cancellationToken)
                => Task.CompletedTask;

            public void Dispose()
            {
            }
        }
    }
}