// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Vocoder;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Storage.Tests;

public sealed class ManagedReceivePreferencesTests : IDisposable
{
    [Fact]
    public async Task MobileStartupAndAlertChoicesRoundTripWithoutCrossingConfigurations()
    {
        var id = ConfigurationId.New();
        var store = new ManagedReceivePreferences(SettingsPath);
        IConsoleConnectionStartupPreferences Scope(ConfigurationId configuration)
            => (IConsoleConnectionStartupPreferences)store.ForConfiguration(configuration, new Dictionary<ChannelId, string>());
        Assert.False(await Scope(id).LoadAutoConnectAsync());
        await Scope(id).SaveAutoConnectAsync(true);
        Assert.True(await Scope(id).LoadAutoConnectAsync());
        Assert.False(await Scope(ConfigurationId.New()).LoadAutoConnectAsync());
        var asset = AssetId.New();
        var tones = await store.LoadToneSettingsAsync();
        tones.MobileAlertPresetName = "Imported alert";
        tones.MobileAlertAssetId = asset.ToString();
        await store.SaveToneSettingsAsync(tones);
        Assert.Equal(asset.ToString(), (await store.LoadToneSettingsAsync()).MobileAlertAssetId);
        var assets = new CleanupAssets();
        Assert.False(await store.DeleteUnreferencedToneAssetAsync(asset, assets));
        Assert.True(await Scope(id).LoadAutoConnectAsync());
        tones.MobileAlertAssetId = null;
        tones.MobileAlertBuiltIn = 1;
        await store.SaveToneSettingsAsync(tones);
        Assert.Equal(1, (await store.LoadToneSettingsAsync()).MobileAlertBuiltIn);
    }

    [Fact]
    public async Task VerboseDiagnosticsRoundTripWithoutReplacingOtherSettings()
    {
        new UserSettingsStore(SettingsPath).Save(new UserSettings { AudioInputGain = 1.7, VerboseLoggingEnabled = true });
        var scope = new ManagedReceivePreferences(SettingsPath).ForConfiguration(ConfigurationId.New(),
            new Dictionary<ChannelId, string>());
        var preferences = Assert.IsAssignableFrom<IConsoleDiagnosticPreferences>(scope);
        Assert.True(await preferences.LoadVerboseLoggingAsync());
        await preferences.SaveVerboseLoggingAsync(false);
        var saved = new UserSettingsStore(SettingsPath).Load();
        Assert.False(saved.VerboseLoggingEnabled);
        Assert.Equal(1.7, saved.AudioInputGain);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preferences.SaveVerboseLoggingAsync(true, new(true)).AsTask());
        Assert.False(await preferences.LoadVerboseLoggingAsync());
    }

    [Fact]
    public async Task ToneAssetCleanupUsesPersistedBackgroundAndToneReferences()
    {
        var background = AssetId.New();
        var tone = AssetId.New();
        var removed = AssetId.New();
        new UserSettingsStore(SettingsPath).Save(new UserSettings
        {
            UserBackgroundAssetId = background.ToString(),
            AlertTones = [new() { Name = "Retained", AssetId = tone.ToString() }]
        });
        var store = new ManagedReceivePreferences(SettingsPath);
        var assets = new CleanupAssets();
        Assert.False(await store.DeleteUnreferencedToneAssetAsync(background, assets));
        Assert.False(await store.DeleteUnreferencedToneAssetAsync(tone, assets));
        Assert.True(await store.DeleteUnreferencedToneAssetAsync(removed, assets));
        Assert.Equal([removed], assets.Deleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToneAssetCleanupRetainsFilesWhenSettingsAreMissingOrCorrupt(bool corrupt)
    {
        if (corrupt)
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(SettingsPath, "{broken");
        }
        var assets = new CleanupAssets();
        await Assert.ThrowsAsync<IOException>(() => new ManagedReceivePreferences(SettingsPath)
            .DeleteUnreferencedToneAssetAsync(AssetId.New(), assets).AsTask());
        Assert.Empty(assets.Deleted);
    }

    [Fact]
    public async Task ToneAssetCleanupHoldsPreferenceGateUntilDeletionFinishes()
    {
        new UserSettingsStore(SettingsPath).Save(new UserSettings());
        var store = new ManagedReceivePreferences(SettingsPath);
        var assets = new CleanupAssets { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Task deleting = store.DeleteUnreferencedToneAssetAsync(AssetId.New(), assets).AsTask();
        try
        {
            await assets.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task saving = store.SaveToneSettingsAsync(new UserSettings { LastDtmfDigits = "42" }).AsTask();
            Assert.False(saving.IsCompleted);
            assets.Release.TrySetResult();
            await Task.WhenAll(deleting, saving).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("42", (await store.LoadToneSettingsAsync()).LastDtmfDigits);
        }
        finally { assets.Release.TrySetResult(); await deleting; }
    }

    private sealed class CleanupAssets : IAssetStore
    {
        public List<AssetId> Deleted { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; init; }
        public async ValueTask<bool> DeleteIfUnreferencedAsync(AssetId id, IReadOnlyCollection<AssetId> referencedAssets,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            if (referencedAssets.Contains(id)) return false;
            Deleted.Add(id);
            return true;
        }
        public ValueTask<AssetDescriptor> ImportAsync(string displayName, string mediaType, Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(AssetId id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<AssetDescriptor> ListAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "neo-preferences-" + Guid.NewGuid().ToString("N"));
    private const string Key = "System\u001FChannel";
    private readonly ChannelId channel = new(new("System", DvmConsole.Core.Runtime.ChannelProtocol.P25, 100, 0, "Channel"));
    private string SettingsPath => Path.Combine(root, "UserSettings.json");

    [Fact]
    public async Task ReceiveBufferingUsesDesktopFallbackAndPreservesOtherConnections()
    {
        var original = new UserSettings { AudioInputGain = 1.5 };
        original.RxJitterBuffer.DmrAdaptive = false;
        original.RxJitterBuffer.DmrMilliseconds = 240;
        original.RxJitterBuffersBySystem["Beta"] = new() { P25Milliseconds = 540, P25Adaptive = false };
        new UserSettingsStore(SettingsPath).Save(original);
        IConsoleReceiveBufferingStore Open() => (IConsoleReceiveBufferingStore)
            new ManagedReceivePreferences(SettingsPath).ForConfiguration(ConfigurationId.New(), new Dictionary<ChannelId, string>());
        var before = await Open().LoadReceiveBufferingAsync(["Alpha", "Beta"]);
        Assert.Equal(240, before["Alpha"].DmrMilliseconds);
        Assert.False(before["Alpha"].DmrAdaptive);
        var changed = before["Alpha"] with { NxdnMilliseconds = 320, NxdnAdaptive = false };
        await Open().SaveReceiveBufferingAsync("Alpha", changed);
        var after = await Open().LoadReceiveBufferingAsync(["alpha", "Beta"]);
        Assert.Equal(changed, after["ALPHA"]);
        Assert.Equal(before["Beta"], after["Beta"]);
        Assert.Equal(1.5, new UserSettingsStore(SettingsPath).Load().AudioInputGain);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Open().SaveReceiveBufferingAsync("Alpha", changed with { DmrMilliseconds = 13 }).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Open().SaveReceiveBufferingAsync("Alpha", new(), new(true)).AsTask());
        Assert.Equal(changed, (await Open().LoadReceiveBufferingAsync(["Alpha"]))["Alpha"]);
    }

    [Fact]
    public async Task RecordingRetentionPreservesAcceptanceAndOtherSettingsAcrossConfigurations()
    {
        new UserSettingsStore(SettingsPath).Save(new UserSettings { AudioInputGain = 1.75 });
        IConsoleRecordingSettingsStore Open() => (IConsoleRecordingSettingsStore)
            new ManagedReceivePreferences(SettingsPath).ForConfiguration(ConfigurationId.New(), new Dictionary<ChannelId, string>());
        Assert.Equal(new RecordingRetentionPolicy(), await Open().LoadRecordingRetentionAsync());
        await Open().SaveRecordingRetentionAsync(new(0, true));
        Assert.Equal(new RecordingRetentionPolicy(0, true), await Open().LoadRecordingRetentionAsync());
        await Open().SaveRecordingRetentionAsync(new(30, false));
        Assert.Equal(new RecordingRetentionPolicy(30, false), await Open().LoadRecordingRetentionAsync());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Open().SaveRecordingRetentionAsync(new(3651, true)).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Open().SaveRecordingRetentionAsync(new(1, true), new(true)).AsTask());
        Assert.Equal(new RecordingRetentionPolicy(30, false), await Open().LoadRecordingRetentionAsync());
        Assert.Equal(1.75, new UserSettingsStore(SettingsPath).Load().AudioInputGain);
    }

    [Fact]
    public async Task ConnectionChimesUseExistingSettingsAndPreserveCancelledWrites()
    {
        new UserSettingsStore(SettingsPath).Save(new UserSettings { AudioInputGain = 1.75, ConnectionChimes = false });
        IConsoleConnectionCuePreferences Open() => (IConsoleConnectionCuePreferences)
            new ManagedReceivePreferences(SettingsPath).ForConfiguration(ConfigurationId.New(), new Dictionary<ChannelId, string>());
        Assert.False(await Open().LoadConnectionChimesAsync());
        await Open().SaveConnectionChimesAsync(true);
        Assert.True(await Open().LoadConnectionChimesAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Open().SaveConnectionChimesAsync(false, new(true)).AsTask());
        Assert.True(await Open().LoadConnectionChimesAsync());
        Assert.Equal(1.75, new UserSettingsStore(SettingsPath).Load().AudioInputGain);
    }

    [Fact]
    public async Task DmrReceiveKeyPolicyUsesExistingSettingsAndPreservesOtherPreferences()
    {
        new UserSettingsStore(SettingsPath).Save(new UserSettings { AudioInputGain = 1.75, RequireConfiguredDmrReceiveKey = true });
        IConsoleDmrReceiveKeyPreferences Open() => (IConsoleDmrReceiveKeyPreferences)
            new ManagedReceivePreferences(SettingsPath).ForConfiguration(ConfigurationId.New(), new Dictionary<ChannelId, string>());
        Assert.True(await Open().LoadRequireConfiguredDmrReceiveKeyAsync());
        await Open().SaveRequireConfiguredDmrReceiveKeyAsync(false);
        Assert.False(await Open().LoadRequireConfiguredDmrReceiveKeyAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Open().SaveRequireConfiguredDmrReceiveKeyAsync(true, new(true)).AsTask());
        Assert.False(await Open().LoadRequireConfiguredDmrReceiveKeyAsync());
        Assert.Equal(1.75, new UserSettingsStore(SettingsPath).Load().AudioInputGain);
    }

    [Fact]
    public async Task ReceiveProfilesPersistIndependentlyAndRejectInvalidNativeOptions()
    {
        new UserSettingsStore(SettingsPath).Save(new UserSettings { AudioInputGain = 1.75 });
        IConsoleReceiveProcessingStore Open() => (IConsoleReceiveProcessingStore)
            new ManagedReceivePreferences(SettingsPath).ForConfiguration(ConfigurationId.New(), new Dictionary<ChannelId, string>());
        var store = Open();
        var before = await store.LoadReceiveProcessingAsync();
        Assert.Equal(4, before.Count);
        var edited = before[VocoderMode.DmrAmbe] with { HighPassFrequencyHz = 180, CompressorEnabled = true, CompressorRatio = 4 };
        await store.SaveReceiveProcessingAsync(VocoderMode.DmrAmbe, edited);
        var saved = await Open().LoadReceiveProcessingAsync();
        Assert.Equal(edited with { HighPassFrequencyHz = 175 }, saved[VocoderMode.DmrAmbe]);
        Assert.Equal(before[VocoderMode.P25Imbe], saved[VocoderMode.P25Imbe]);
        Assert.Equal(1.75, new UserSettingsStore(SettingsPath).Load().AudioInputGain);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveReceiveProcessingAsync(VocoderMode.DmrAmbe,
            edited with { HighPassFrequencyHz = float.NaN }).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveReceiveProcessingAsync(VocoderMode.DmrAmbe, new(), new(true)).AsTask());
        Assert.Equal(saved[VocoderMode.DmrAmbe], (await Open().LoadReceiveProcessingAsync())[VocoderMode.DmrAmbe]);
    }

    [Fact]
    public async Task MicrophonePresetsReplaceNamesAndDeleteWithoutChangingAppliedAudio()
    {
        new UserSettingsStore(SettingsPath).Save(new UserSettings { AudioInputGain = 1.75, AudioInputAgcEnabled = true });
        IConsoleMicrophonePresetStore Open() => (IConsoleMicrophonePresetStore)
            new ManagedReceivePreferences(SettingsPath).ForConfiguration(ConfigurationId.New(), new Dictionary<ChannelId, string>());
        var store = Open();
        var first = await store.SaveMicrophonePresetAsync(" Field ", new() { Gain = 2, LowGainDb = -2 });
        Assert.Equal("Field", first.SelectedName);
        Assert.Equal(2, Assert.Single(first.Presets).Gain);
        var replaced = await store.SaveMicrophonePresetAsync("FIELD", new() { Gain = 0.5 });
        Assert.Equal(0.5, Assert.Single(replaced.Presets).Gain);
        Assert.Equal(2, Assert.Single(first.Presets).Gain);
        Assert.Equal(replaced.Presets.ToArray(), (await Open().LoadMicrophonePresetsAsync()).Presets.ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveMicrophonePresetAsync("Invalid", new() { Gain = double.NaN }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveMicrophonePresetAsync(new string('x', 81), new()).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteMicrophonePresetAsync("FIELD", new(true)).AsTask());
        Assert.Single((await Open().LoadMicrophonePresetsAsync()).Presets);
        var deleted = await store.DeleteMicrophonePresetAsync("field");
        Assert.Empty(deleted.Presets);
        Assert.Empty(deleted.SelectedName);
        Assert.Empty((await Open().LoadMicrophonePresetsAsync()).Presets);
        var original = new UserSettingsStore(SettingsPath).Load();
        Assert.Equal(1.75, original.AudioInputGain);
        Assert.True(original.AudioInputAgcEnabled);
        Assert.Equal("Mic preset 1", (await store.SaveMicrophonePresetAsync("", new())).SelectedName);
    }

    [Fact]
    public async Task MicrophoneProcessingRoundTripsWithoutChangingDesktopRouteOrMode()
    {
        var original = new UserSettings
        {
            AudioInputDeviceId = "desktop-input",
            AudioProcessingMode = UserSettings.WindowsCommunicationsProcessingMode,
            TalkPermitTone = true
        };
        new UserSettingsStore(SettingsPath).Save(original);
        IConsoleMicrophoneProcessingStore Open() => (IConsoleMicrophoneProcessingStore)
            new ManagedReceivePreferences(SettingsPath).ForConfiguration(ConfigurationId.New(), new Dictionary<ChannelId, string>());
        await Open().SaveMicrophoneProcessingAsync(new()
        {
            Gain = 1.5,
            LowGainDb = -3,
            MidGainDb = 2,
            HighGainDb = 4,
            AgcEnabled = true,
            AgcTargetDbfs = -24
        });
        var saved = await Open().LoadMicrophoneProcessingAsync();
        Assert.Equal(1.5, saved.Gain);
        Assert.Equal(-3, saved.LowGainDb);
        Assert.Equal(2, saved.MidGainDb);
        Assert.Equal(4, saved.HighGainDb);
        Assert.True(saved.AgcEnabled);
        Assert.Equal(-24, saved.AgcTargetDbfs);
        var settings = new UserSettingsStore(SettingsPath).Load();
        Assert.Equal("desktop-input", settings.AudioInputDeviceId);
        Assert.Equal(UserSettings.WindowsCommunicationsProcessingMode, settings.AudioProcessingMode);
        Assert.True(settings.TalkPermitTone);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Open().SaveMicrophoneProcessingAsync(new(), new(true)).AsTask());
        Assert.Equal(1.5, (await Open().LoadMicrophoneProcessingAsync()).Gain);
    }

    [Fact]
    public async Task ManualAudioChoicesUseExistingSettingsAndPreserveUnrelatedValues()
    {
        var original = new UserSettings
        {
            TalkPermitTone = true,
            MuteRxAudioWhileTransmitting = false,
            AudioOutputDeviceId = "desktop-output"
        };
        new UserSettingsStore(SettingsPath).Save(original);
        IConsoleManualTransmitOptionsStore Open() => (IConsoleManualTransmitOptionsStore)
            new ManagedReceivePreferences(SettingsPath).ForConfiguration(ConfigurationId.New(), new Dictionary<ChannelId, string>());
        var options = Open();
        Assert.Equal(new ConsoleManualTransmitOptions(true, false), await options.LoadManualTransmitOptionsAsync());
        await options.SaveManualTransmitOptionsAsync(new(false, true));
        Assert.Equal(new ConsoleManualTransmitOptions(false, true), await Open().LoadManualTransmitOptionsAsync());
        Assert.Equal("desktop-output", new UserSettingsStore(SettingsPath).Load().AudioOutputDeviceId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => options.SaveManualTransmitOptionsAsync(new(true, false), new(true)).AsTask());
        Assert.Equal(new ConsoleManualTransmitOptions(false, true), await Open().LoadManualTransmitOptionsAsync());
    }

    [Fact]
    public async Task WebStreamAuthorizationSurvivesReopenButNotChangedCredentialsOrConfiguration()
    {
        var id = ConfigurationId.New();
        var stream = new WebStreamPlaybackDescriptor(WebStreamId.FromIdentity("Feed", "https://example.invalid/audio"),
            "Feed", "https://example.invalid/audio", "listener", "fixture-secret", 1, null);
        var original = new UserSettings();
        original.ConfigurationOperatorStates[id.ToString()] = new()
        {
            WebStreamOutputDeviceIds = new() { ["Feed"] = "desktop-output" },
            ChannelVolumes = new() { [Key] = 0.5 }
        };
        new UserSettingsStore(SettingsPath).Save(original);
        var store = new ManagedReceivePreferences(SettingsPath);
        var scope = store.ForConfiguration(id, new Dictionary<ChannelId, string>());
        var web = Assert.IsAssignableFrom<IConsoleWebStreamPreferences>(scope);
        await web.SaveWebStreamAsync(stream, selected: true, volume: 0.75);
        var reopened = (IConsoleWebStreamPreferences)new ManagedReceivePreferences(SettingsPath)
            .ForConfiguration(id, new Dictionary<ChannelId, string>());
        Assert.Equal(new ConsoleWebStreamPreference(0.75, true), (await reopened.LoadWebStreamsAsync([stream]))[stream.Id]);
        foreach (var changed in new[] { stream with { Url = "https://example.invalid/other" },
                     stream with { AuthUsername = "other" }, stream with { AuthPassword = "changed" } })
            Assert.False((await reopened.LoadWebStreamsAsync([changed]))[changed.Id].RestoreSelected);
        var other = (IConsoleWebStreamPreferences)store.ForConfiguration(ConfigurationId.New(), new Dictionary<ChannelId, string>());
        Assert.False((await other.LoadWebStreamsAsync([stream]))[stream.Id].RestoreSelected);
        var startup = (IConsoleListeningStartupPreferences)scope;
        await startup.SaveRestoreSelectedChannelsAsync(false, default);
        await web.SaveWebStreamAsync(stream, volume: 1.5);
        Assert.False((await reopened.LoadWebStreamsAsync([stream]))[stream.Id].RestoreSelected);
        await startup.SaveRestoreSelectedChannelsAsync(true, default);
        Assert.True((await reopened.LoadWebStreamsAsync([stream]))[stream.Id].RestoreSelected);
        var persisted = new UserSettingsStore(SettingsPath).Load().ConfigurationOperatorStates[id.ToString()];
        Assert.Equal("desktop-output", persisted.WebStreamOutputDeviceIds["Feed"]);
        Assert.Equal(0.5, persisted.ChannelVolumes[Key]);
        Assert.DoesNotContain("fixture-secret", File.ReadAllText(SettingsPath));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => web.SaveWebStreamAsync(stream,
            selected: false, cancellationToken: new(true)).AsTask());
        Assert.True((await reopened.LoadWebStreamsAsync([stream]))[stream.Id].RestoreSelected);
        await web.SaveWebStreamAsync(stream, selected: false);
        Assert.False((await reopened.LoadWebStreamsAsync([stream]))[stream.Id].RestoreSelected);
    }

    [Fact]
    public async Task PartialWritesPreserveDesktopFieldsAndOtherConfigurationsAcrossReopen()
    {
        var first = ConfigurationId.New();
        var second = ConfigurationId.New();
        var original = new UserSettings();
        original.ConfigurationOperatorStates[first.ToString()] = new()
        {
            ChannelOutputDeviceIds = new() { [Key] = "desktop-device" },
            TransmitSelectedChannelKeys = [Key],
            RecordingIgnoredSubscriberIds = new() { [Key] = [123] },
            ChannelVolumes = new() { [Key] = 0.5 }
        };
        new UserSettingsStore(SettingsPath).Save(original);
        var store = new ManagedReceivePreferences(SettingsPath);
        var a = store.ForConfiguration(first, new Dictionary<ChannelId, string> { [channel] = Key });
        var b = store.ForConfiguration(second, new Dictionary<ChannelId, string> { [channel] = Key });
        await Task.WhenAll(
            a.SaveAsync(channel, new(RecordingEnabled: true, ReceiveEnabled: true), default).AsTask(),
            b.SaveAsync(channel, new(Gain: 2, Balance: -0.5), default).AsTask());
        await a.SaveAsync(channel, new(Balance: 0.25), default);
        var reopened = new ManagedReceivePreferences(SettingsPath).ForConfiguration(first,
            new Dictionary<ChannelId, string> { [channel] = Key });
        var restored = (await reopened.LoadAsync(default))[channel];
        Assert.Equal(new ChannelReceivePreferences(0.5, 0.25, true, true), restored with { IgnoredSubscriberIds = default });
        Assert.Equal(new uint[] { 123 }, restored.IgnoredSubscriberIds.ToArray());
        var other = (await b.LoadAsync(default))[channel];
        Assert.Equal(new ChannelReceivePreferences(2, -0.5), other with { IgnoredSubscriberIds = default });
        Assert.Empty(other.IgnoredSubscriberIds);
        var persisted = new UserSettingsStore(SettingsPath).Load().ConfigurationOperatorStates[first.ToString()];
        Assert.Equal("desktop-device", persisted.ChannelOutputDeviceIds[Key]);
        Assert.Equal([Key], persisted.TransmitSelectedChannelKeys);
        Assert.Equal([123u], persisted.RecordingIgnoredSubscriberIds[Key]);
    }

    [Fact]
    public async Task DisabledSelectionRestoreDoesNotEraseSavedReceiveIntentWhenVolumeChanges()
    {
        var id = ConfigurationId.New();
        var settings = new UserSettings();
        settings.ConfigurationOperatorStates[id.ToString()] = new()
        { RestoreSelectedChannelsOnStartup = false, ReceiveEnabledChannelKeys = [Key], RecordingEnabledChannelKeys = [Key] };
        new UserSettingsStore(SettingsPath).Save(settings);
        var scope = new ManagedReceivePreferences(SettingsPath).ForConfiguration(id,
            new Dictionary<ChannelId, string> { [channel] = Key });
        var loaded = (await scope.LoadAsync(default))[channel];
        Assert.False(loaded.ReceiveEnabled);
        Assert.True(loaded.RecordingEnabled);
        await scope.SaveAsync(channel, new(Gain: 0.75), default);
        Assert.Contains(Key, new UserSettingsStore(SettingsPath).Load().ConfigurationOperatorStates[id.ToString()].ReceiveEnabledChannelKeys);
        var startup = Assert.IsAssignableFrom<IConsoleListeningStartupPreferences>(scope);
        Assert.False(await startup.LoadRestoreSelectedChannelsAsync(default));
        await startup.SaveRestoreSelectedChannelsAsync(true, default);
        var reopened = new ManagedReceivePreferences(SettingsPath).ForConfiguration(id,
            new Dictionary<ChannelId, string> { [channel] = Key });
        Assert.True((await reopened.LoadAsync(default))[channel].ReceiveEnabled);
        Assert.True((await reopened.LoadAsync(default))[channel].RecordingEnabled);
        Assert.True(await ((IConsoleListeningStartupPreferences)reopened).LoadRestoreSelectedChannelsAsync(default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.SaveRestoreSelectedChannelsAsync(false, new(true)).AsTask());
        Assert.True(await startup.LoadRestoreSelectedChannelsAsync(default));
    }

    [Fact]
    public async Task TransmitChoicesShareTheDesktopEnvelopeWithoutRestoringActiveIntent()
    {
        var id = ConfigurationId.New();
        var store = new ManagedReceivePreferences(SettingsPath);
        var scope = store.ForConfiguration(id, new Dictionary<ChannelId, string> { [channel] = Key });
        var transmit = Assert.IsAssignableFrom<IConsoleTransmitPreferences>(scope);
        await transmit.SaveTransmitAsync(channel, new(Selected: true, Encrypted: false), default);
        await scope.SaveAsync(channel, new(Gain: 0.75), default);
        await ((IConsoleListeningStartupPreferences)scope).SaveRestoreSelectedChannelsAsync(false, default);
        var reopened = store.ForConfiguration(id, new Dictionary<ChannelId, string> { [channel] = Key });
        var loaded = (await ((IConsoleTransmitPreferences)reopened).LoadTransmitAsync(default))[channel];
        Assert.Equal(new ChannelTransmitPreferences(false, false), loaded);
        var saved = new UserSettingsStore(SettingsPath).Load().ConfigurationOperatorStates[id.ToString()];
        Assert.Contains(Key, saved.TransmitSelectedChannelKeys);
        Assert.False(saved.TransmitEncryptionStates[Key]);
        Assert.Equal(0.75, saved.ChannelVolumes[Key]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transmit.SaveTransmitAsync(channel,
            new(Encrypted: true), new CancellationToken(true)).AsTask());
        await ((IConsoleListeningStartupPreferences)reopened).SaveRestoreSelectedChannelsAsync(true, default);
        Assert.Equal(new ChannelTransmitPreferences(true, false),
            (await ((IConsoleTransmitPreferences)reopened).LoadTransmitAsync(default))[channel]);
    }

    [Fact]
    public async Task ToneWritesPreserveOtherSettingsAndPresetHoldDurations()
    {
        var original = new UserSettings { LocalToneMonitorEnabled = false, RecordingRetentionDays = 31 };
        original.ChannelOutputDeviceIds[Key] = "desktop-device";
        new UserSettingsStore(SettingsPath).Save(original);
        var store = new ManagedReceivePreferences(SettingsPath);
        var working = await store.LoadToneSettingsAsync();
        string assetId = Guid.NewGuid().ToString("D");
        working.AlertTones = [new() { Name = "Custom alert", AssetId = assetId, FileName = "alert.wav" }];
        working.LocalToneMonitorEnabled = true;
        working.MobileAlertPresetName = "Main alert";
        working.DtmfPresets = [new() { Name = "Precise hold", Digits = "1", Steps =
            [new() { Digit = "1", DurationSeconds = 0.25 }, new() { Kind = AudioPresetStepKinds.Hold, DurationSeconds = 0.02 }] }];
        await store.SaveToneSettingsAsync(working);
        working.DtmfPresets[0].Steps[1].DurationSeconds = 9;
        var saved = new UserSettingsStore(SettingsPath).Load();
        Assert.Equal(0.02, saved.DtmfPresets[0].Steps[1].DurationSeconds);
        Assert.True(saved.LocalToneMonitorEnabled);
        Assert.Equal("Main alert", (await store.LoadToneSettingsAsync()).MobileAlertPresetName);
        Assert.Equal(Guid.Parse(assetId), Guid.Parse(Assert.Single(saved.AlertTones).AssetId!));
        working.AlertTones[0].Name = "Unsaved edit";
        Assert.Equal("Custom alert", Assert.Single((await store.LoadToneSettingsAsync()).AlertTones).Name);
        Assert.Equal(31, saved.RecordingRetentionDays);
        Assert.Equal("desktop-device", saved.ChannelOutputDeviceIds[Key]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveToneSettingsAsync(working, new(true)).AsTask());
        Assert.Equal(0.02, (await store.LoadToneSettingsAsync()).DtmfPresets[0].Steps[1].DurationSeconds);
    }

    [Fact]
    public async Task CancelledWriteLeavesExistingFileUnchanged()
    {
        var id = ConfigurationId.New();
        var scope = new ManagedReceivePreferences(SettingsPath).ForConfiguration(id,
            new Dictionary<ChannelId, string> { [channel] = Key });
        await scope.SaveAsync(channel, new(Gain: 0.75), default);
        byte[] before = File.ReadAllBytes(SettingsPath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.SaveAsync(channel,
            new(Gain: 2), new CancellationToken(true)).AsTask());
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public async Task GroupEditsPreserveOtherGroupsAndDoNotEraseDisabledStartupIntent()
    {
        var id = ConfigurationId.New();
        var store = new ManagedReceivePreferences(SettingsPath);
        var scope = store.ForConfiguration(id, new Dictionary<ChannelId, string> { [channel] = Key });
        var groups = Assert.IsAssignableFrom<IConsoleGroupPreferences>(scope);
        var members = new List<PatchMemberSetting> { new() { SystemName = "System", ChannelName = "Channel", DestinationId = 100 } };
        await groups.SaveGroupAsync("Dispatch", members, true, true);
        members[0].DestinationId = 999;
        await groups.SaveGroupAsync("Multi select", [], true, false);
        await groups.SaveRestorePatchesAsync(false);
        await scope.SaveAsync(channel, new(Gain: 0.75), default);
        var reopened = Assert.IsAssignableFrom<IConsoleGroupPreferences>(new ManagedReceivePreferences(SettingsPath)
            .ForConfiguration(id, new Dictionary<ChannelId, string> { [channel] = Key }));
        var saved = await reopened.LoadGroupsAsync();
        Assert.False(saved.RestorePatchesOnStartup);
        Assert.True(saved.Groups.EnabledStates["Dispatch"]);
        Assert.True(saved.Groups.OneWayModes["Dispatch"]);
        Assert.Equal(100u, Assert.Single(saved.Groups.Memberships["Dispatch"]).DestinationId);
        Assert.Contains("Multi select", saved.Groups.Memberships.Keys);
        saved.Groups.Memberships.Clear();
        Assert.Equal(2, (await reopened.LoadGroupsAsync()).Groups.Memberships.Count);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reopened.SaveRestorePatchesAsync(true, new(true)).AsTask());
        Assert.False((await reopened.LoadGroupsAsync()).RestorePatchesOnStartup);
    }

    [Fact]
    public async Task UnreadableSettingsPreventSilentDefaultStartup()
    {
        Directory.CreateDirectory(SettingsPath);
        var scope = new ManagedReceivePreferences(SettingsPath).ForConfiguration(ConfigurationId.New(),
            new Dictionary<ChannelId, string> { [channel] = Key });
        await Assert.ThrowsAsync<IOException>(() => scope.LoadAsync(default).AsTask());
        Assert.True(Directory.Exists(SettingsPath));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
