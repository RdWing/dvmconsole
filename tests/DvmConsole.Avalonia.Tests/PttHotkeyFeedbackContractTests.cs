// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Core.Configuration;
using DvmConsole.Platform.Hotkeys;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class PttHotkeyFeedbackContractTests
    {
        private static readonly HotkeyGesture GestureF9 = new(
            HotkeyKey.F9, HotkeyModifiers.Control);

        [Fact]
        public void Feedback_IsEmptyByDefaultAndDuringConfiguredHydration()
        {
            var empty = CreateViewModel();
            Assert.Empty(empty.PttHotkeyFeedback);

            using var dir = new TempDir();
            var persistence = new PttSettingsPersistence(
                new SettingsSectionStore(dir.SettingsPath));
            persistence.Save(new UserSettingsPttSection
            {
                GlobalPTTShortcut = 0x20041,
            });

            var hydrated = new MainWindowViewModel(
                null,
                null,
                new UnavailableGlobalHotkeyService(),
                null,
                null,
                null,
                null,
                null,
                persistence);

            Assert.Equal(
                new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Control),
                hydrated.Ptt!.Hotkey);
            Assert.Empty(hydrated.PttHotkeyFeedback);
        }

        [Fact]
        public void Feedback_MapsStatusesToFixedTextAndIsChangeOnly()
        {
            var viewModel = CreateViewModel();
            var notifications = 0;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.PttHotkeyFeedback))
                {
                    notifications++;
                }
            };

            viewModel.ReportPttHotkeyStatus(
                HotkeyRegistrationStatus.PermissionDenied,
                GestureF9);
            Assert.Equal(
                "Global hotkey permission required.",
                viewModel.PttHotkeyFeedback);
            viewModel.ReportPttHotkeyStatus(
                HotkeyRegistrationStatus.PermissionDenied,
                GestureF9);
            Assert.Equal(1, notifications);

            viewModel.ReportPttHotkeyStatus(
                HotkeyRegistrationStatus.Unsupported,
                GestureF9);
            Assert.Equal(
                "Global hotkey unavailable on this host.",
                viewModel.PttHotkeyFeedback);
            Assert.Equal(2, notifications);

            viewModel.ReportPttHotkeyStatus(
                HotkeyRegistrationStatus.AlreadyRegistered,
                GestureF9);
            Assert.Empty(viewModel.PttHotkeyFeedback);
            Assert.Equal(3, notifications);

            viewModel.ReportPttHotkeyStatus(
                HotkeyRegistrationStatus.Registered,
                GestureF9);
            Assert.Equal(3, notifications);
        }

        private static MainWindowViewModel CreateViewModel()
            => new(null, null, new UnavailableGlobalHotkeyService());

        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-ptt-feedback-" + Guid.NewGuid().ToString("N"));

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
    }
}
