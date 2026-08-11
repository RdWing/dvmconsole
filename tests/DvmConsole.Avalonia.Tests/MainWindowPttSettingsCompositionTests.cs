// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Hotkeys;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Contract for the headless PTT settings composition boundary.
    /// The dashboard loads persisted PTT state into the already-composed PTT
    /// capability view-model and persists effective post-hydration PTT
    /// changes through the shared section store.
    /// </summary>
    public sealed class MainWindowPttSettingsCompositionTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-main-window-ptt-settings-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(Root);

            public string SettingsPath => Path.Combine(Root, "UserSettings.json");

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root))
                        Directory.Delete(Root, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; never mask the test result.
                }
            }
        }

        [Fact]
        public void FullConstructor_LoadsPersistedModeScopeAndSupportedHotkey()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsPttSection
            {
                TogglePTTMode = true,
                GlobalPTTShortcut = 0x20041,
                GlobalPTTKeysAllChannels = true
            });
            var hotkeys = new RecordingHotkeyService();

            var vm = CreateViewModel(hotkeys, persistence);

            Assert.NotNull(vm.Ptt);
            Assert.True(vm.Ptt!.ToggleMode);
            Assert.True(vm.Ptt.AllChannels);
            Assert.Equal(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Control), vm.Ptt.Hotkey);
            Assert.Equal(1, hotkeys.CapabilityCalls);
        }

        [Fact]
        public void UnsupportedPersistedHotkey_LeavesHotkeyClearedButLoadsOtherState()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsPttSection
            {
                TogglePTTMode = true,
                GlobalPTTShortcut = 0x60,
                GlobalPTTKeysAllChannels = true
            });

            var vm = CreateViewModel(new RecordingHotkeyService(), persistence);

            Assert.NotNull(vm.Ptt);
            Assert.True(vm.Ptt!.ToggleMode);
            Assert.True(vm.Ptt.AllChannels);
            Assert.Null(vm.Ptt.Hotkey);
        }

        [Fact]
        public void MalformedLoad_DegradesToPttDefaultsWithoutThrowing()
        {
            using var dir = new TempDir();
            File.WriteAllText(dir.SettingsPath, "{ not valid json");

            var exception = Record.Exception(() =>
            {
                var vm = CreateViewModel(new RecordingHotkeyService(), CreatePersistence(dir.SettingsPath));

                Assert.NotNull(vm.Ptt);
                Assert.False(vm.Ptt!.ToggleMode);
                Assert.False(vm.Ptt.AllChannels);
                Assert.Null(vm.Ptt.Hotkey);
            });

            Assert.Null(exception);
        }

        [Fact]
        public void NullPersistence_PreservesUnseededPttComposition()
        {
            var vm = CreateViewModel(new RecordingHotkeyService(), null);

            Assert.NotNull(vm.Ptt);
            Assert.False(vm.Ptt!.ToggleMode);
            Assert.False(vm.Ptt.AllChannels);
            Assert.Null(vm.Ptt.Hotkey);
        }

        [Fact]
        public void CompositionPersistsPostHydrationHotkeyChangesWithoutHydrationWrite()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsPttSection { GlobalPTTShortcut = 0x20041 });
            var vm = CreateViewModel(new RecordingHotkeyService(), persistence);

            vm.Ptt!.SetHotkey(new HotkeyGesture(HotkeyKey.F2, HotkeyModifiers.None));

            Assert.True(persistence.TryLoad(out var stored));
            Assert.Equal(0x71, stored.GlobalPTTShortcut);
        }

        private static MainWindowViewModel CreateViewModel(
            IGlobalHotkeyService hotkeys,
            PttSettingsPersistence? persistence)
            => new(
                null,
                null,
                hotkeys,
                null,
                null,
                null,
                null,
                null,
                persistence);

        private static PttSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));

        private sealed class RecordingHotkeyService : IGlobalHotkeyService
        {
            public int CapabilityCalls { get; private set; }

            public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

            public HotkeyCapability GetCapability(HotkeyGesture gesture)
            {
                CapabilityCalls++;
                return HotkeyCapability.Available;
            }

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
