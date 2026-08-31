using Avalonia.Media;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Ptt;
using System.Globalization;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed partial class SystemViewModelTests
{
    [Fact]
    public async Task LoadsCallPriorityFromConfigurationScopedOperatorSettings()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        var settings = new UserSettings();
        CodeplugStudioStateStore.Get(settings, codeplugPath).CallPrioritySystemNames.Add("Alpha");
        store.Save(settings);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);

            SystemViewModel alpha = Assert.Single(viewModel.Systems, system => system.Name == "Alpha");
            SystemViewModel beta = Assert.Single(viewModel.Systems, system => system.Name == "Beta");
            Assert.True(alpha.HasCallPriority);
            Assert.All(alpha.Channels, channel => Assert.True(channel.HasCallPriority));
            Assert.False(beta.HasCallPriority);
            Assert.All(beta.Channels, channel => Assert.False(channel.HasCallPriority));
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task PersistsAndRestoresTheSelectedChannel()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = Path.Combine(Path.GetTempPath(), "dvmconsole-settings-tests", $"{Guid.NewGuid():N}", "UserSettings.json");
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using (MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store))
            {
                viewModel.SelectChannel(viewModel.Systems[1].Channels[1]);
            }

            Assert.Equal("Beta\u001FBeta Operations", store.Load().LastSelectedChannelKey);

            await using MainWindowViewModel restored = MainWindowViewModel.Load(codeplugPath, store);
            Assert.Equal("Beta Operations", restored.SelectedChannel?.Name);
            Assert.Equal("Beta", restored.SelectedSystem?.Name);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task LaunchWithoutAPathRestoresTheLastCodeplug()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = Path.Combine(Path.GetTempPath(), "dvmconsole-settings-tests", $"{Guid.NewGuid():N}", "UserSettings.json");
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings { LastCodeplugPath = codeplugPath });

        try
        {
            await using MainWindowViewModel restored = MainWindowViewModel.Load(null, store);

            Assert.Equal(2, restored.Systems.Count);
            Assert.Equal(["Alpha", "Beta"], restored.Systems.Select(system => system.Name));
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task SuccessfulLoadsMaintainBoundedRecentCodeplugHistory()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string otherPath = Path.Combine(Path.GetTempPath(), "dvmconsole-other-codeplug.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings { RecentCodeplugPaths = [otherPath, codeplugPath] });

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);

            Assert.Equal([Path.GetFullPath(codeplugPath), Path.GetFullPath(otherPath)], viewModel.RecentCodeplugPaths);
            await viewModel.FlushUserSettingsAsync();
            Assert.Equal(viewModel.RecentCodeplugPaths, store.Load().RecentCodeplugPaths);
            Assert.False(viewModel.HasCodeplugDiagnostics);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task ConnectionDiagnosticsTrackMediaPacketCountsAndMetadata()
    {
        var system = new SystemViewModel(
            new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Test",
            "127.0.0.1:62031");
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.P25,
            1,
            1001,
            2002,
            null,
            "GROUP",
            "VOICE",
            "LDU1",
            7,
            42,
            new byte[] { 1, 2, 3 });

        try
        {
            system.RecordTraffic(traffic);

            Assert.Equal("Media this connection · RX 3 B · TX 0 B", system.TrafficTotalsText);
            Assert.Equal("Test ○", system.SystemTabText);
            Assert.Contains("P25 GROUP/VOICE", system.StreamTrafficText);
            Assert.Contains("1 packets / 3 B", system.StreamTrafficText);
            Assert.Contains("stream 42", system.StreamTrafficText);
            Assert.Contains("1001→2002", system.StreamTrafficText);

            system.ApplyStatus(new FneConnectionStatus(
                "Test",
                FneConnectionState.Connected,
                "FNE peer connected",
                DateTimeOffset.UtcNow));
            Assert.Equal("Test ●", system.SystemTabText);
        }
        finally
        {
            await system.DisposeAsync();
        }
    }

    [Fact]
    public async Task RestoresAndImmediatelyPersistsRxJitterBufferIndependentlyPerConnection()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings
        {
            RxJitterBuffer = new RxJitterBufferSetting { P25Adaptive = false },
            RxJitterBuffersBySystem = new Dictionary<string, RxJitterBufferSetting>
            {
                ["Alpha"] = new()
                {
                    P25Milliseconds = 360,
                    DmrMilliseconds = 60,
                    NxdnMilliseconds = 80,
                    P25Adaptive = false,
                    DmrAdaptive = true
                }
            }
        });

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            SystemViewModel alpha = Assert.Single(viewModel.Systems, system => system.Name == "Alpha");
            SystemViewModel beta = Assert.Single(viewModel.Systems, system => system.Name == "Beta");

            Assert.Equal(360, alpha.GetConfiguredJitterBuffer().P25Milliseconds);
            Assert.Equal(60, alpha.GetConfiguredJitterBuffer().DmrMilliseconds);
            Assert.Equal(80, alpha.GetConfiguredJitterBuffer().NxdnMilliseconds);
            Assert.True(alpha.GetConfiguredJitterBuffer().DmrAdaptive);
            Assert.Equal(RxJitterBufferSetting.DefaultP25Milliseconds, beta.GetConfiguredJitterBuffer().P25Milliseconds);

            RxJitterBufferModeViewModel betaP25 = Assert.Single(
                beta.RxJitterBufferModes,
                mode => mode.Protocol == RxJitterBufferProtocol.P25);
            Assert.Contains(
                betaP25.Options,
                option => !option.IsAdaptive && option.Milliseconds == 720);
            Assert.Contains(
                betaP25.Options,
                option => option.IsAdaptive && option.Label == "Adaptive ≤ 1620 ms");
            betaP25.SelectedOption = Assert.Single(betaP25.Options, option => option.IsAdaptive);

            await viewModel.FlushUserSettingsAsync();
            UserSettings persisted = store.Load();
            Assert.Equal(360, persisted.RxJitterBuffersBySystem["Alpha"].P25Milliseconds);
            Assert.False(persisted.RxJitterBuffersBySystem["Alpha"].P25Adaptive);
            Assert.Equal(RxJitterBufferSetting.DefaultP25Milliseconds, persisted.RxJitterBuffersBySystem["Beta"].P25Milliseconds);
            Assert.True(persisted.RxJitterBuffersBySystem["Beta"].P25Adaptive);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task SystemReceiveActivityRequiresEnabledReceivePresentation()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "Test",
            Tgid = "2002",
            Mode = "p25"
        });
        var system = new SystemViewModel(
            new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Test",
            "127.0.0.1:62031",
            [channel],
            [],
            accentIndex: 1);
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.P25,
            1,
            1001,
            2002,
            null,
            "GROUP",
            "VOICE",
            "LDU1",
            7,
            42,
            []);

        try
        {
            Assert.Equal("○", system.StatusGlyph);
            Assert.False(system.IsReceiving);
            Assert.True(channel.TryApplyTraffic("Test", traffic));
            Assert.False(system.IsReceiving);

            channel.SetAudioEnabled(true);

            Assert.True(system.IsReceiving);
            Assert.Equal(1.0, system.ActivityBarOpacity);
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(SystemAccentPalette.GetBrush(1)).Color,
                Assert.IsType<SolidColorBrush>(system.StatusAccentBrush).Color);
        }
        finally
        {
            await system.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectionDiagnosticsCoalesceRepeatedPacketNotifications()
    {
        var system = new SystemViewModel(
            new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Test",
            "127.0.0.1:62031");
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.P25, 1, 1001, 2002, null, "GROUP", "VOICE", "LDU1", 7, 42,
            new byte[] { 1, 2, 3 });
        int notifications = 0;
        system.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SystemViewModel.TrafficTotalsText) or nameof(SystemViewModel.StreamTrafficText))
                notifications++;
        };

        try
        {
            for (int index = 0; index < 20; index++)
                system.RecordTraffic(traffic, publishDiagnostics: false);

            Assert.Equal(0, notifications);
            Assert.Contains("20 packets / 60 B", system.StreamTrafficText);

            system.PublishTrafficDiagnostics();

            Assert.Equal(2, notifications);
            Assert.Equal("Media this connection · RX 60 B · TX 0 B", system.TrafficTotalsText);
            Assert.Contains("20 packets / 60 B", system.StreamTrafficText);
        }
        finally
        {
            await system.DisposeAsync();
        }
    }

    [Fact]
    public async Task SavesUsesAndDeletesMicrophonePresets()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            viewModel.TalkPermitTone = true;
            await viewModel.FlushUserSettingsAsync();
            Assert.True(store.Load().TalkPermitTone);
            Assert.True(viewModel.LocalToneMonitorEnabled);
            viewModel.LocalToneMonitorEnabled = false;
            await viewModel.FlushUserSettingsAsync();
            Assert.False(store.Load().LocalToneMonitorEnabled);
            viewModel.VerboseLoggingEnabled = false;
            viewModel.VerboseLoggingEnabled = true;
            await viewModel.FlushUserSettingsAsync();
            Assert.True(store.Load().VerboseLoggingEnabled);
            viewModel.DarkMode = true;
            await viewModel.FlushUserSettingsAsync();
            Assert.True(store.Load().DarkMode);
            Assert.True(viewModel.ConnectionChimes);
            viewModel.UiFontSize = 16;
            viewModel.UiScale = 1.25;
            await viewModel.FlushUserSettingsAsync();
            Assert.Equal(16, store.Load().UiFontSize);
            Assert.Equal(1.25, store.Load().UiScale);
            Assert.Equal(1.25, viewModel.UiScaleTransform.ScaleX);
            Assert.Equal(1.25, viewModel.UiScaleTransform.ScaleY);
            viewModel.AudioInputPresetNameText = " Field ";
            viewModel.AudioInputGainText = "1.25";
            viewModel.AudioInputLowGainText = "-2";
            viewModel.AudioInputMidGainText = "1";
            viewModel.AudioInputHighGainText = "3";

            viewModel.SaveAudioInputPreset();

            AudioInputPresetViewModel preset = Assert.Single(viewModel.AudioInputPresets);
            Assert.Equal("Field", preset.Name);
            await viewModel.FlushUserSettingsAsync();
            Assert.Equal("Field", store.Load().AudioInputPresetName);
            Assert.Equal(1.25, store.Load().AudioInputPresets[0].Gain);

            viewModel.AudioInputGainText = "2";
            viewModel.UseAudioInputPreset(preset);
            Assert.Equal("1.25", viewModel.AudioInputGainText);
            Assert.Equal("-2", viewModel.AudioInputLowGainText);

            viewModel.DeleteAudioInputPreset(preset);
            Assert.Empty(viewModel.AudioInputPresets);
            await viewModel.FlushUserSettingsAsync();
            Assert.Empty(store.Load().AudioInputPresets);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task PersistsPlatformAppropriateAudioProcessingMode()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            viewModel.AudioInputDeviceIdText = "input-device-42";
            viewModel.AudioOutputDeviceIdText = "output-device-84";
            viewModel.AudioInputAgcEnabled = true;
            viewModel.AudioInputAgcTargetDbfsText = "-30";
            viewModel.SelectedAudioProcessingMode = OperatingSystem.IsWindows()
                ? "Windows communications processing"
                : "DVM Console processing";
            viewModel.ApplyAudioInputSettingsCommand.Execute(null);
            await viewModel.FlushUserSettingsAsync();

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    ["DVM Console processing", "Windows communications processing"],
                    viewModel.AudioProcessingModeOptions);
                Assert.DoesNotContain("Apple voice processing", viewModel.AudioProcessingModeOptions);
                Assert.Equal("Windows communications processing", viewModel.SelectedAudioProcessingMode);
                Assert.False(viewModel.IsDvmConsoleProcessingSelected);
                Assert.False(viewModel.IsAgcTargetEnabled);
                UserSettings windowsSettings = store.Load();
                Assert.Equal(
                    UserSettings.WindowsCommunicationsProcessingMode,
                    windowsSettings.AudioProcessingMode);
                Assert.Equal("input-device-42", windowsSettings.AudioInputDeviceId);
                Assert.Equal("output-device-84", windowsSettings.AudioOutputDeviceId);
                Assert.Contains("depend on Windows", viewModel.AudioProcessingDescription, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("bypassed", viewModel.AudioProcessingDescription, StringComparison.OrdinalIgnoreCase);

                viewModel.SelectedAudioProcessingMode = "DVM Console processing";
                viewModel.ApplyAudioInputSettingsCommand.Execute(null);
                Assert.True(viewModel.IsDvmConsoleProcessingSelected);
                await viewModel.FlushUserSettingsAsync();
                Assert.Equal(UserSettings.DvmConsoleAudioProcessingMode, store.Load().AudioProcessingMode);
                return;
            }

            Assert.Single(viewModel.AudioProcessingModeOptions);
            Assert.Equal("DVM Console processing", viewModel.AudioProcessingModeOptions[0]);
            Assert.DoesNotContain("Apple voice processing", viewModel.AudioProcessingModeOptions);
            Assert.Equal("DVM Console processing", viewModel.SelectedAudioProcessingMode);
            Assert.True(viewModel.IsDvmConsoleProcessingSelected);
            UserSettings portableSettings = store.Load();
            Assert.Equal(UserSettings.DvmConsoleAudioProcessingMode, portableSettings.AudioProcessingMode);
            Assert.Equal("input-device-42", portableSettings.AudioInputDeviceId);
            Assert.Equal("output-device-84", portableSettings.AudioOutputDeviceId);
            Assert.True(portableSettings.AudioInputAgcEnabled);
            Assert.Equal(-30, portableSettings.AudioInputAgcTargetDbfs);
            Assert.DoesNotContain("echo cancellation", viewModel.AudioProcessingDescription, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task NormalizesSavedAppleVoiceProcessingToDvmConsoleAtStartup()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings
        {
            AudioProcessingMode = UserSettings.AppleVoiceProcessingMode
        });

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);

            Assert.Equal("DVM Console processing", viewModel.SelectedAudioProcessingMode);
            Assert.True(viewModel.IsDvmConsoleProcessingSelected);
            Assert.DoesNotContain("Apple voice processing", viewModel.AudioProcessingModeOptions);
            await viewModel.FlushUserSettingsAsync();
            Assert.Equal(
                UserSettings.DvmConsoleAudioProcessingMode,
                store.Load().AudioProcessingMode);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task PersistsAndFormatsToolbarClockSettings()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);

            Assert.True(viewModel.ClockUse24HourTime);
            Assert.True(viewModel.ClockShowSeconds);
            Assert.True(viewModel.IsConfiguredPttKey(KeyboardPttKey.Space));
            Assert.False(viewModel.IsConfiguredPttKey(KeyboardPttKey.F1));
            Assert.Equal("13:05:09", MainWindowViewModel.FormatClock(
                new DateTime(2026, 8, 14, 13, 5, 9),
                use24HourTime: true,
                showSeconds: true));
            Assert.Equal(new DateTime(2026, 8, 14, 13, 5, 9).ToString("h:mm tt", CultureInfo.CurrentCulture), MainWindowViewModel.FormatClock(
                new DateTime(2026, 8, 14, 13, 5, 9),
                use24HourTime: false,
                showSeconds: false));

            viewModel.ClockUse24HourTime = false;
            viewModel.ClockShowSeconds = false;
            viewModel.KeepWindowOnTop = true;
            viewModel.TogglePttMode = true;
            Assert.Contains(KeyboardPttKey.F3, viewModel.GlobalPttKeyOptions);
            Assert.Contains(KeyboardPttKey.F19, viewModel.GlobalPttKeyOptions);
            Assert.Contains(KeyboardPttKey.None, viewModel.GlobalPttKeyOptions);
            viewModel.SelectedGlobalPttKey = KeyboardPttKey.F3;
            await viewModel.ApplyGlobalPttKeySelectionAsync();
            viewModel.SelectedActiveSystemPttKey = KeyboardPttKey.F4;
            await viewModel.ApplyActiveSystemPttKeySelectionAsync();

            await viewModel.FlushUserSettingsAsync();
            UserSettings saved = store.Load();
            Assert.False(saved.ClockUse24HourTime);
            Assert.False(saved.ClockShowSeconds);
            Assert.True(saved.KeepWindowOnTop);
            Assert.True(saved.TogglePttMode);
            Assert.Equal("F3", saved.GlobalPttKey);
            Assert.Equal("F4", saved.ActiveSystemPttKey);
            Assert.True(viewModel.IsConfiguredPttKey(KeyboardPttKey.F3));
            Assert.True(viewModel.IsConfiguredPttKey(KeyboardPttKey.F4));

            viewModel.SelectedGlobalPttKey = KeyboardPttKey.F4;
            await viewModel.ApplyGlobalPttKeySelectionAsync();
            await viewModel.FlushUserSettingsAsync();
            Assert.Equal("F3", store.Load().GlobalPttKey);
            Assert.Contains("already assigned", viewModel.TransmitStatusText);

            viewModel.SelectedGlobalPttKey = KeyboardPttKey.None;
            await viewModel.ApplyGlobalPttKeySelectionAsync();
            await viewModel.FlushUserSettingsAsync();
            Assert.Equal("None", store.Load().GlobalPttKey);
            Assert.Equal("Keyboard PTT disabled", viewModel.GlobalPttKeyText);

            viewModel.SelectedActiveSystemPttKey = KeyboardPttKey.None;
            await viewModel.ApplyActiveSystemPttKeySelectionAsync();
            await viewModel.FlushUserSettingsAsync();
            Assert.Equal("None", store.Load().ActiveSystemPttKey);
            Assert.Equal("Keyboard PTT disabled", viewModel.ActiveSystemPttKeyText);
            Assert.NotEmpty(viewModel.ClockText);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

}
