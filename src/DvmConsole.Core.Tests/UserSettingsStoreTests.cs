using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Core.Tests;

public sealed class UserSettingsStoreTests
{
    [Theory]
    [InlineData("F19", "F19")]
    [InlineData("None", "None")]
    [InlineData("unsupported", "None")]
    public void NormalizesOptionalGlobalPttKeys(string configured, string expected)
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);
            store.Save(new UserSettings { GlobalPttKey = configured });

            Assert.Equal(expected, store.Load().GlobalPttKey);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData("F19", "F19")]
    [InlineData("None", "None")]
    [InlineData("unsupported", "None")]
    public void NormalizesOptionalActiveSystemPttKeys(string configured, string expected)
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);
            store.Save(new UserSettings { ActiveSystemPttKey = configured });

            Assert.Equal(expected, store.Load().ActiveSystemPttKey);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void DisablesAnActiveSystemPttKeyThatDuplicatesGlobalPtt()
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);
            store.Save(new UserSettings
            {
                GlobalPttKey = "F12",
                ActiveSystemPttKey = "f12"
            });

            UserSettings loaded = store.Load();
            Assert.Equal("F12", loaded.GlobalPttKey);
            Assert.Equal("None", loaded.ActiveSystemPttKey);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void MissingSettingsReturnDefaults()
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);

            UserSettings settings = store.Load();

            Assert.Null(settings.LastCodeplugPath);
            Assert.Null(settings.LastSelectedChannelKey);
            Assert.True(settings.ConnectionChimes);
            Assert.False(settings.HighQualityBluetoothAudioEnabled);
            Assert.False(settings.KeepTransmitMicrophoneWarm);
            Assert.Equal(4, settings.RxAudioProcessingOptions.Count);
            Assert.All(settings.RxAudioProcessingOptions.Values, option =>
            {
                Assert.True(option.HighPassFilterEnabled);
                Assert.Equal(250, option.HighPassFrequencyHz);
                Assert.True(option.PeakingFilterEnabled);
                Assert.Equal(2_500, option.PeakingFrequencyHz);
                Assert.Equal(3, option.PeakingGainDb);
                Assert.False(option.CompressorEnabled);
                Assert.Equal(3, option.CompressorRatio);
                Assert.Equal(-18, option.CompressorThresholdDbfs);
                Assert.Equal(3, option.CompressorMakeupGainDb);
            });
            Assert.Equal(14, settings.UiFontSize);
            Assert.Equal(1.0, settings.UiScale);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void WarmMicrophonePersistsTheLastDisabledState()
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);
            var settings = new UserSettings { KeepTransmitMicrophoneWarm = true };
            store.Save(settings);

            settings.KeepTransmitMicrophoneWarm = false;
            store.Save(settings);

            Assert.False(store.Load().KeepTransmitMicrophoneWarm);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void NormalizesPersistedChannelStereoBalances()
    {
        string path = CreatePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                {
                  "SchemaVersion": 2,
                  "ChannelStereoBalances": {
                    " Alpha\u001FLeft ": -2.0,
                    "Alpha\u001FRight": 2.0,
                    " ": 0.5
                  }
                }
                """);

            UserSettings settings = new UserSettingsStore(path).Load();
            var property = typeof(UserSettings).GetProperty("ChannelStereoBalances");

            Assert.NotNull(property);
            var balances = Assert.IsType<Dictionary<string, double>>(property.GetValue(settings));
            Assert.Equal(-1.0, balances["Alpha\u001FLeft"]);
            Assert.Equal(1.0, balances["Alpha\u001FRight"]);
            Assert.Equal(2, balances.Count);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void LegacyBluetoothDefaultRequiresFreshOptIn()
    {
        string path = CreatePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                {
                  "SchemaVersion": 1,
                  "HighQualityBluetoothAudioEnabled": true
                }
                """);

            UserSettings settings = new UserSettingsStore(path).Load();

            Assert.Equal(UserSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.False(settings.HighQualityBluetoothAudioEnabled);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void LegacyDisabledRxProcessingMigratesToDisabledOptionalStages()
    {
        string path = CreatePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                {
                  "SchemaVersion": 2,
                  "RxAudioProcessingEnabled": false
                }
                """);

            UserSettings settings = new UserSettingsStore(path).Load();

            Assert.Equal(UserSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Null(settings.LegacyRxAudioProcessingEnabled);
            Assert.All(settings.RxAudioProcessingOptions.Values, option =>
            {
                Assert.False(option.HighPassFilterEnabled);
                Assert.False(option.PeakingFilterEnabled);
                Assert.False(option.CompressorEnabled);
            });
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void NormalizesRxProcessingRangesAndFrequencySteps()
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);
            store.Save(new UserSettings
            {
                RxAudioProcessingOptions = new Dictionary<string, RxAudioProcessingModeSetting>
                {
                    [RxAudioProcessingModeSetting.DmrMode] = new()
                    {
                        HighPassFrequencyHz = 288,
                        PeakingFrequencyHz = 263,
                        PeakingGainDb = 12,
                        CompressorRatio = 0.5,
                        CompressorThresholdDbfs = -50,
                        CompressorMakeupGainDb = 12
                    }
                }
            });

            RxAudioProcessingModeSetting dmr = store.Load().RxAudioProcessingOptions[
                RxAudioProcessingModeSetting.DmrMode];

            Assert.Equal(300, dmr.HighPassFrequencyHz);
            Assert.Equal(275, dmr.PeakingFrequencyHz);
            Assert.Equal(10, dmr.PeakingGainDb);
            Assert.Equal(1, dmr.CompressorRatio);
            Assert.Equal(-40, dmr.CompressorThresholdDbfs);
            Assert.Equal(10, dmr.CompressorMakeupGainDb);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void NormalizesCallHistoryPaneAndWindowPlacement()
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);
            store.Save(new UserSettings
            {
                ShowCallHistoryPane = false,
                SnapCallHistoryToWindow = true,
                CallHistoryWindowPlacement = new WindowPlacementSetting
                {
                    Left = double.NaN,
                    Top = 42,
                    Width = 100,
                    Height = 5000
                }
            });

            UserSettings loaded = store.Load();

            Assert.False(loaded.ShowCallHistoryPane);
            Assert.True(loaded.SnapCallHistoryToWindow);
            Assert.Null(loaded.CallHistoryWindowPlacement.Left);
            Assert.Equal(42, loaded.CallHistoryWindowPlacement.Top);
            Assert.Equal(400, loaded.CallHistoryWindowPlacement.Width);
            Assert.Equal(1400, loaded.CallHistoryWindowPlacement.Height);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void NormalizesInterfaceSizing()
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);
            store.Save(new UserSettings
            {
                UiFontSize = 100,
                UiScale = 0.1
            });

            UserSettings loaded = store.Load();

            Assert.Equal(20, loaded.UiFontSize);
            Assert.Equal(0.75, loaded.UiScale);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void SettingsRoundTripThroughAnAtomicJsonFile()
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);
            store.Save(new UserSettings
            {
                LastCodeplugPath = "/tmp/codeplug.yml",
                RecentCodeplugPaths = [" /tmp/one.yml ", "/tmp/ONE.yml", "/tmp/two.yml"],
                LastSelectedSystemName = "System 1",
                LastSelectedChannelKey = "System 1\u001FDispatch",
                AudioInputDeviceId = " microphone-1 ",
                AudioOutputDeviceId = " speaker-1 ",
                RxAudioProcessingOptions = new Dictionary<string, RxAudioProcessingModeSetting>
                {
                    [RxAudioProcessingModeSetting.DmrMode] = new()
                    {
                        HighPassFilterEnabled = false,
                        HighPassFrequencyHz = 300,
                        PeakingFrequencyHz = 2_000,
                        PeakingGainDb = -2.5,
                        CompressorEnabled = true,
                        CompressorRatio = 4,
                        CompressorThresholdDbfs = -24,
                        CompressorMakeupGainDb = 4.5
                    }
                },
                AudioProcessingMode = UserSettings.AppleVoiceProcessingMode,
                HighQualityBluetoothAudioEnabled = false,
                AudioInputAgcEnabled = true,
                AudioInputAgcTargetDbfs = -30,
                KeepTransmitMicrophoneWarm = true,
                AudioInputGain = 1.5,
                AudioInputEqLowGainDb = -3,
                AudioInputEqMidGainDb = 2,
                AudioInputEqHighGainDb = 4,
                AudioInputPresetName = " Voice ",
                AudioInputPresets =
                [
                    new AudioInputPresetSetting
                    {
                        Name = " Voice ",
                        Gain = 1.25,
                        LowGainDb = -2,
                        MidGainDb = 1,
                        HighGainDb = 3
                    }
                ],
                ToolbarClocks =
                [
                    new ToolbarClockSetting
                    {
                        Enabled = true,
                        UtcOffsetHours = 5,
                        ColorHex = "#0D47A1"
                    }
                ],
                MuteRxAudioWhileTransmitting = false,
                TalkPermitTone = true,
                ConnectionChimes = true,
                UiFontSize = 16,
                UiScale = 1.2,
                DarkMode = true,
                ClockUse24HourTime = false,
                ClockShowSeconds = false,
                KeepWindowOnTop = true,
                ShowSystemStatus = false,
                ShowChannels = false,
                ShowAlertTones = false,
                LockWidgets = false,
                ChannelWidgetPositions = new Dictionary<string, WidgetPositionSetting>
                {
                    [" System 1\u001FDispatch "] = new WidgetPositionSetting { X = 125, Y = 240 }
                },
                UserBackgroundImage = " /tmp/background.png ",
                TogglePttMode = true,
                GlobalPttKey = " f12 ",
                ActiveSystemPttKey = " f11 ",
                SerialPttEnabled = true,
                SerialPttActiveSystemOnly = true,
                SerialPttPortName = " /dev/cu.ptt ",
                SerialPttBaudRate = 19_200,
                ReceiveEnabledChannelKeys = [" System 1\u001FDispatch ", "system 1\u001Fdispatch"],
                TransmitSelectedChannelKeys = [" System 1\u001FDispatch ", "system 1\u001Fdispatch"],
                LastDtmfDigits = " 12a# ",
                ToneFrequencyHz = 1200,
                ToneDurationSeconds = 2.5,
                DtmfPresets =
                [
                    new DtmfPresetSetting { Name = " Gate ", Digits = " 12a# " }
                ],
                TonePresets =
                [
                    new TonePresetSetting { Name = " Alert ", FrequencyHz = 1200, DurationSeconds = 2.5 }
                ],
                AlertTones =
                [
                    new AlertToneSetting { Name = " Evacuate ", FilePath = " /tmp/evacuate.wav " }
                ],
                RecordingRetentionDays = 14,
                RecordingRootPath = " /tmp/recordings ",
                ChannelVolumes = new Dictionary<string, double>
                {
                    [" System 1\u001FDispatch "] = 1.5
                },
                ChannelOutputDeviceIds = new Dictionary<string, string>
                {
                    [" System 1\u001FDispatch "] = " speaker-2 "
                },
                WebStreamVolumes = new Dictionary<string, double>
                {
                    [" News "] = 1.25
                },
                WebStreamOutputDeviceIds = new Dictionary<string, string>
                {
                    [" News "] = " web-stream-output-1 "
                },
                RecordingIgnoredSubscriberIds = new Dictionary<string, List<uint>>
                {
                    [" System 1\u001FDispatch "] = [42, 42, 0]
                },
                RecordingEnabledChannelKeys = [" System 1\u001FDispatch ", "system 1\u001fdispatch"],
                PatchGroupMemberships = new Dictionary<string, List<PatchMemberSetting>>
                {
                    [" Dispatch "] =
                    [
                        new PatchMemberSetting { SystemName = "Alpha", DestinationId = 100 },
                        new PatchMemberSetting { SystemName = "alpha", DestinationId = 100 },
                        new PatchMemberSetting { SystemName = "Beta", DestinationId = 200 }
                    ]
                },
                PatchGroupModes = new Dictionary<string, bool> { [" Dispatch "] = true },
                PatchGroupEnabledStates = new Dictionary<string, bool> { [" Dispatch "] = true },
                RetainPatchStateOnStartup = true,
                RestoreSelectedChannelsOnStartup = false,
                SelectedWebStreams = [" News ", "news", " Alerts "],
                TransmitEncryptionStates = new Dictionary<string, bool>
                {
                    ["System 1\u001FEncrypted"] = false
                }
            });

            UserSettings loaded = store.Load();

            Assert.Equal("/tmp/codeplug.yml", loaded.LastCodeplugPath);
            Assert.Equal(UserSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(
                [Path.GetFullPath("/tmp/one.yml"), Path.GetFullPath("/tmp/two.yml")],
                loaded.RecentCodeplugPaths);
            Assert.Equal("System 1", loaded.LastSelectedSystemName);
            Assert.Equal("System 1\u001FDispatch", loaded.LastSelectedChannelKey);
            Assert.Equal("microphone-1", loaded.AudioInputDeviceId);
            Assert.Equal("speaker-1", loaded.AudioOutputDeviceId);
            RxAudioProcessingModeSetting dmrRx = loaded.RxAudioProcessingOptions[
                RxAudioProcessingModeSetting.DmrMode];
            Assert.False(dmrRx.HighPassFilterEnabled);
            Assert.Equal(300, dmrRx.HighPassFrequencyHz);
            Assert.True(dmrRx.PeakingFilterEnabled);
            Assert.Equal(2_000, dmrRx.PeakingFrequencyHz);
            Assert.Equal(-2.5, dmrRx.PeakingGainDb);
            Assert.True(dmrRx.CompressorEnabled);
            Assert.Equal(4, dmrRx.CompressorRatio);
            Assert.Equal(-24, dmrRx.CompressorThresholdDbfs);
            Assert.Equal(4.5, dmrRx.CompressorMakeupGainDb);
            Assert.Equal(UserSettings.AppleVoiceProcessingMode, loaded.AudioProcessingMode);
            Assert.False(loaded.HighQualityBluetoothAudioEnabled);
            Assert.True(loaded.AudioInputAgcEnabled);
            Assert.Equal(-30, loaded.AudioInputAgcTargetDbfs);
            Assert.True(loaded.KeepTransmitMicrophoneWarm);
            Assert.Equal(1.5, loaded.AudioInputGain);
            Assert.Equal(-3, loaded.AudioInputEqLowGainDb);
            Assert.Equal(2, loaded.AudioInputEqMidGainDb);
            Assert.Equal(4, loaded.AudioInputEqHighGainDb);
            Assert.Equal("Voice", loaded.AudioInputPresetName);
            AudioInputPresetSetting microphonePreset = Assert.Single(loaded.AudioInputPresets);
            Assert.Equal("Voice", microphonePreset.Name);
            Assert.Equal(1.25, microphonePreset.Gain);
            Assert.Equal(-2, microphonePreset.LowGainDb);
            Assert.Equal(1, microphonePreset.MidGainDb);
            Assert.Equal(3, microphonePreset.HighGainDb);
            Assert.True(loaded.ToolbarClocks[0].Enabled);
            Assert.Equal(5, loaded.ToolbarClocks[0].UtcOffsetHours);
            Assert.Equal("#0D47A1", loaded.ToolbarClocks[0].ColorHex);
            Assert.False(loaded.MuteRxAudioWhileTransmitting);
            Assert.True(loaded.TalkPermitTone);
            Assert.True(loaded.ConnectionChimes);
            Assert.Equal(16, loaded.UiFontSize);
            Assert.Equal(1.2, loaded.UiScale);
            Assert.True(loaded.DarkMode);
            Assert.False(loaded.ClockUse24HourTime);
            Assert.False(loaded.ClockShowSeconds);
            Assert.True(loaded.KeepWindowOnTop);
            Assert.False(loaded.ShowSystemStatus);
            Assert.False(loaded.ShowChannels);
            Assert.False(loaded.ShowAlertTones);
            Assert.False(loaded.LockWidgets);
            WidgetPositionSetting widgetPosition = Assert.Single(loaded.ChannelWidgetPositions).Value;
            Assert.Equal(125, widgetPosition.X);
            Assert.Equal(240, widgetPosition.Y);
            Assert.Equal("/tmp/background.png", loaded.UserBackgroundImage);
            Assert.True(loaded.TogglePttMode);
            Assert.Equal("F12", loaded.GlobalPttKey);
            Assert.Equal("F11", loaded.ActiveSystemPttKey);
            Assert.True(loaded.SerialPttEnabled);
            Assert.True(loaded.SerialPttActiveSystemOnly);
            Assert.Equal("/dev/cu.ptt", loaded.SerialPttPortName);
            Assert.Equal(19_200, loaded.SerialPttBaudRate);
            Assert.Equal(["System 1\u001FDispatch"], loaded.ReceiveEnabledChannelKeys);
            Assert.Equal(["System 1\u001FDispatch"], loaded.TransmitSelectedChannelKeys);
            Assert.Equal("12A#", loaded.LastDtmfDigits);
            Assert.Equal(1200, loaded.ToneFrequencyHz);
            Assert.Equal(2.5, loaded.ToneDurationSeconds);
            Assert.Equal("Gate", loaded.DtmfPresets[0].Name);
            Assert.Equal("12A#", loaded.DtmfPresets[0].Digits);
            Assert.Equal(["1", "2", "A", "#"], loaded.DtmfPresets[0].Steps.Select(step => step.Digit));
            Assert.Equal("Alert", loaded.TonePresets[0].Name);
            Assert.Equal(1200, loaded.TonePresets[0].FrequencyHz);
            Assert.Equal(2.5, loaded.TonePresets[0].DurationSeconds);
            Assert.Single(loaded.TonePresets[0].Steps);
            AlertToneSetting alertTone = Assert.Single(loaded.AlertTones);
            Assert.Equal("Evacuate", alertTone.Name);
            Assert.Equal("/tmp/evacuate.wav", alertTone.FilePath);
            Assert.Equal(1.5, loaded.ChannelVolumes["System 1\u001FDispatch"]);
            Assert.Equal("speaker-2", loaded.ChannelOutputDeviceIds["System 1\u001FDispatch"]);
            Assert.Equal(1.25, loaded.WebStreamVolumes["News"]);
            Assert.Equal("web-stream-output-1", loaded.WebStreamOutputDeviceIds["News"]);
            Assert.Equal(14, loaded.RecordingRetentionDays);
            Assert.Equal(Path.GetFullPath("/tmp/recordings"), loaded.RecordingRootPath);
            Assert.Equal(["System 1\u001FDispatch"], loaded.RecordingEnabledChannelKeys);
            Assert.Equal([42u], loaded.RecordingIgnoredSubscriberIds["System 1\u001FDispatch"]);
            Assert.Equal(2, loaded.PatchGroupMemberships["Dispatch"].Count);
            Assert.True(loaded.PatchGroupModes["Dispatch"]);
            Assert.True(loaded.PatchGroupEnabledStates["Dispatch"]);
            Assert.True(loaded.RetainPatchStateOnStartup);
            Assert.False(loaded.RestoreSelectedChannelsOnStartup);
            Assert.Equal(["Alerts", "News"], loaded.SelectedWebStreams);
            Assert.False(loaded.TransmitEncryptionStates["System 1\u001FEncrypted"]);
            Assert.True(File.Exists(path));
            Assert.DoesNotContain(".tmp", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void MalformedSettingsFallBackToDefaults()
    {
        string path = CreatePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not valid json");
            var store = new UserSettingsStore(path);

            UserSettings loaded = store.Load();

            Assert.Null(loaded.LastCodeplugPath);
            Assert.Null(loaded.LastSelectedChannelKey);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void InvalidSerialPttSettingsFailSafeToDisabledDefaults()
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);
            store.Save(new UserSettings
            {
                SerialPttEnabled = true,
                SerialPttPortName = "   ",
                SerialPttBaudRate = -1
            });

            UserSettings loaded = store.Load();

            Assert.False(loaded.SerialPttEnabled);
            Assert.Equal(string.Empty, loaded.SerialPttPortName);
            Assert.Equal(9_600, loaded.SerialPttBaudRate);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ExportsImportsAndResetsPortableSettings()
    {
        string path = CreatePath();
        string exportPath = Path.Combine(Path.GetDirectoryName(path)!, "exported.json");
        string importedPath = Path.Combine(Path.GetDirectoryName(path)!, "imported.json");
        try
        {
            var store = new UserSettingsStore(path);
            store.Export(new UserSettings
            {
                LastCodeplugPath = "/tmp/original.yml",
                TalkPermitTone = true,
                GlobalPttKey = "F4",
                ActiveSystemPttKey = "F5",
                SerialPttEnabled = true,
                SerialPttActiveSystemOnly = true,
                SerialPttPortName = "/dev/cu.original",
                SerialPttBaudRate = 9_600
            }, exportPath);

            Assert.True(File.Exists(exportPath));
            Assert.True(store.Load().TalkPermitTone);

            File.WriteAllText(importedPath, """
                {
                  "lastCodeplugPath": "/tmp/imported.yml",
                  "talkPermitTone": false,
                  "globalPttKey": "f9",
                  "activeSystemPttKey": "f10",
                  "serialPttEnabled": true,
                  "serialPttActiveSystemOnly": true,
                  "serialPttPortName": " /dev/cu.imported ",
                  "serialPttBaudRate": 38400
                }
                """);
            UserSettings imported = store.Import(importedPath);

            Assert.Equal("/tmp/imported.yml", imported.LastCodeplugPath);
            Assert.False(imported.TalkPermitTone);
            Assert.Equal("F9", imported.GlobalPttKey);
            Assert.Equal("F10", imported.ActiveSystemPttKey);
            Assert.True(imported.SerialPttEnabled);
            Assert.True(imported.SerialPttActiveSystemOnly);
            Assert.Equal("/dev/cu.imported", imported.SerialPttPortName);
            Assert.Equal(38_400, imported.SerialPttBaudRate);

            store.Reset();
            Assert.False(File.Exists(path));
            Assert.Null(store.Load().LastCodeplugPath);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ImportRejectsMalformedSettingsWithoutReplacingCurrentProfile()
    {
        string path = CreatePath();
        string importPath = Path.Combine(Path.GetDirectoryName(path)!, "malformed.json");
        try
        {
            var store = new UserSettingsStore(path);
            store.Save(new UserSettings { GlobalPttKey = "F3" });
            File.WriteAllText(importPath, "{ not json");

            Assert.Throws<InvalidDataException>(() => store.Import(importPath));
            Assert.Equal("F3", store.Load().GlobalPttKey);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void NormalizesMalformedAudioPresetsOnLoad()
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);
            store.Save(new UserSettings
            {
                DtmfPresets =
                [
                    new DtmfPresetSetting { Name = " ", Digits = "invalid" }
                ],
                TonePresets =
                [
                    new TonePresetSetting { Name = " ", FrequencyHz = 50_000, DurationSeconds = -2 }
                ],
                AudioInputDeviceId = " ",
                AudioOutputDeviceId = " ",
                AudioProcessingMode = "invalid",
                AudioInputAgcTargetDbfs = -100,
                AudioInputGain = 50,
                AudioInputEqLowGainDb = -50,
                AudioInputEqMidGainDb = double.NaN,
                AudioInputEqHighGainDb = 50,
                AudioInputPresetName = " Voice ",
                AudioInputPresets =
                [
                    new AudioInputPresetSetting
                    {
                        Name = " ",
                        Gain = 50,
                        LowGainDb = -50,
                        MidGainDb = double.NaN,
                        HighGainDb = 50
                    }
                ]
            });

            UserSettings loaded = store.Load();

            Assert.Equal("DTMF Preset", loaded.DtmfPresets[0].Name);
            Assert.Equal("123", loaded.DtmfPresets[0].Digits);
            Assert.Equal("Tone Preset", loaded.TonePresets[0].Name);
            Assert.Equal(1000, loaded.TonePresets[0].FrequencyHz);
            Assert.Equal(1.0, loaded.TonePresets[0].DurationSeconds);
            Assert.Equal("default", loaded.AudioInputDeviceId);
            Assert.Equal("default", loaded.AudioOutputDeviceId);
            Assert.Equal(UserSettings.DvmConsoleAudioProcessingMode, loaded.AudioProcessingMode);
            Assert.Equal(-40, loaded.AudioInputAgcTargetDbfs);
            Assert.Equal(3, loaded.AudioInputGain);
            Assert.Equal(-12, loaded.AudioInputEqLowGainDb);
            Assert.Equal(0, loaded.AudioInputEqMidGainDb);
            Assert.Equal(12, loaded.AudioInputEqHighGainDb);
            Assert.Equal("Voice", loaded.AudioInputPresetName);
            AudioInputPresetSetting microphonePreset = Assert.Single(loaded.AudioInputPresets);
            Assert.Equal("Mic Preset", microphonePreset.Name);
            Assert.Equal(3, microphonePreset.Gain);
            Assert.Equal(-12, microphonePreset.LowGainDb);
            Assert.Equal(0, microphonePreset.MidGainDb);
            Assert.Equal(12, microphonePreset.HighGainDb);
            Assert.Equal(["1", "2", "3"], loaded.DtmfPresets[0].Steps.Select(step => step.Digit));
            Assert.Equal(AudioPresetStepKinds.Tone, loaded.TonePresets[0].Steps[0].Kind);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void NormalizesStepBasedPresetsAndPreservesHolds()
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);
            store.Save(new UserSettings
            {
                DtmfPresets =
                [
                    new DtmfPresetSetting
                    {
                        Name = "Gate",
                        Steps =
                        [
                            new DtmfPresetStepSetting { Kind = "digit", Digit = "a", DurationSeconds = 0.1 },
                            new DtmfPresetStepSetting { Kind = "hold", DurationSeconds = 99 },
                            new DtmfPresetStepSetting { Kind = "digit", Digit = "#", DurationSeconds = 0.5 }
                        ]
                    }
                ],
                TonePresets =
                [
                    new TonePresetSetting
                    {
                        Name = "Alert",
                        Steps =
                        [
                            new TonePresetStepSetting { Kind = "tone", FrequencyHz = 1200, DurationSeconds = 0.1 },
                            new TonePresetStepSetting { Kind = "hold", FrequencyHz = 99, DurationSeconds = 0.75 }
                        ]
                    }
                ]
            });

            UserSettings loaded = store.Load();

            Assert.Equal(["A", string.Empty, "#"], loaded.DtmfPresets[0].Steps.Select(step => step.Digit));
            Assert.Equal(
                [AudioPresetStepKinds.Digit, AudioPresetStepKinds.Hold, AudioPresetStepKinds.Digit],
                loaded.DtmfPresets[0].Steps.Select(step => step.Kind));
            Assert.Equal(10.0, loaded.DtmfPresets[0].Steps[1].DurationSeconds);
            Assert.Equal(
                [AudioPresetStepKinds.Tone, AudioPresetStepKinds.Hold],
                loaded.TonePresets[0].Steps.Select(step => step.Kind));
            Assert.Equal(1200, loaded.TonePresets[0].FrequencyHz);
            Assert.Equal(0.25, loaded.TonePresets[0].Steps[0].DurationSeconds);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void NamedProfilesRoundTripAndSelectiveImportPreservesSession()
    {
        string path = CreatePath();
        string importPath = Path.Combine(Path.GetDirectoryName(path)!, "imported-profile.json");
        try
        {
            var store = new UserSettingsStore(path);
            var profile = new UserSettings
            {
                TalkPermitTone = true,
                AudioOutputDeviceId = "profile-output",
                LastCodeplugPath = "/tmp/profile.yml",
                DtmfPresets = [new DtmfPresetSetting { Name = "Night" }]
            };
            store.SaveNamedProfile("Night Shift", profile);

            Assert.Equal(["Night Shift"], store.ListNamedProfiles());
            Assert.True(store.LoadNamedProfile("Night Shift").TalkPermitTone);

            var importStore = new UserSettingsStore(importPath);
            importStore.Save(profile);
            SettingsImportPreview preview = store.PreviewImport(importPath);

            Assert.Equal(UserSettings.CurrentSchemaVersion, preview.SchemaVersion);
            Assert.Equal("/tmp/profile.yml", preview.LastCodeplugPath);
            Assert.Contains("General", preview.PopulatedSections);
            Assert.Contains("Audio", preview.PopulatedSections);
            Assert.Contains("Presets", preview.PopulatedSections);

            store.Save(new UserSettings
            {
                AudioOutputDeviceId = "current-output",
                LastCodeplugPath = "/tmp/current.yml"
            });
            store.Import(importPath, SettingsImportScope.OperatorState);
            UserSettings merged = store.Load();

            Assert.True(merged.TalkPermitTone);
            Assert.Equal("profile-output", merged.AudioOutputDeviceId);
            Assert.Equal("/tmp/current.yml", merged.LastCodeplugPath);

            store.DeleteNamedProfile("Night Shift");
            Assert.Empty(store.ListNamedProfiles());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void NamedProfileNamesCannotEscapeProfilesDirectory()
    {
        string path = CreatePath();
        try
        {
            var store = new UserSettingsStore(path);

            Assert.Throws<ArgumentException>(() => store.SaveNamedProfile("../outside", new UserSettings()));
            Assert.Throws<ArgumentException>(() => store.PreviewNamedProfile("../outside"));
            Assert.Throws<ArgumentException>(() => store.SaveNamedProfile("Shift:Night", new UserSettings()));
            Assert.Throws<ArgumentException>(() => store.SaveNamedProfile("CON", new UserSettings()));
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static string CreatePath()
    {
        return Path.Combine(Path.GetTempPath(), "dvmconsole-settings-tests", $"{Guid.NewGuid():N}", "UserSettings.json");
    }

    private static void Cleanup(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (File.Exists(path))
            File.Delete(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
