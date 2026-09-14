// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;
using DvmConsole.Application;

namespace DvmConsole.Storage;

public sealed partial class ManagedReceivePreferences
{
    public async ValueTask<bool> DeleteUnreferencedToneAssetAsync(AssetId id, IAssetStore assets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assets);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Hold the same gate as preference writes until deletion completes.
            // The editor copy excludes background settings and cannot prove that
            // an asset is unreferenced in the persisted settings envelope.
            var references = await Task.Run(() =>
            {
                var store = new UserSettingsStore(settingsPath);
                var settings = store.Load();
                if (store.LastReadState != SettingsReadState.Loaded || store.LastLoadDiagnostics.RecoveredFromBackup)
                    throw new IOException("Saved settings are unavailable; alert audio was retained.");
                return ConsoleAssetReferences.FromSettings(settings);
            }, cancellationToken).ConfigureAwait(false);
            return await assets.DeleteIfUnreferencedAsync(id, references, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public ValueTask<UserSettings> LoadToneSettingsAsync(CancellationToken cancellationToken = default)
        => AccessAsync(settings => CopyTones(settings, new UserSettings()), false, cancellationToken);

    public async ValueTask SaveToneSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        // Capture before yielding: the editor may continue changing its working copy.
        var captured = CopyTones(settings, new UserSettings());
        await AccessAsync(current => CopyTones(captured, current), true, cancellationToken).ConfigureAwait(false);
    }

    private static UserSettings CopyTones(UserSettings source, UserSettings destination)
    {
        destination.MobileAlertPresetName = source.MobileAlertPresetName;
        destination.MobileAlertBuiltIn = source.MobileAlertBuiltIn;
        destination.MobileAlertAssetId = source.MobileAlertAssetId;
        destination.LastDtmfDigits = source.LastDtmfDigits;
        destination.LocalToneMonitorEnabled = source.LocalToneMonitorEnabled;
        destination.AlertTones = source.AlertTones.Select(tone => new AlertToneSetting
        { Name = tone.Name, AssetId = tone.AssetId, FileName = tone.FileName, FilePath = tone.FilePath }).ToList();
        destination.ToneFrequencyHz = source.ToneFrequencyHz;
        destination.ToneDurationSeconds = source.ToneDurationSeconds;
        destination.QuickCallToneAFrequencyHz = source.QuickCallToneAFrequencyHz;
        destination.QuickCallToneBFrequencyHz = source.QuickCallToneBFrequencyHz;
        destination.DtmfPresets = source.DtmfPresets.Select(preset => new DtmfPresetSetting
        {
            Name = preset.Name,
            Digits = preset.Digits,
            Steps = preset.Steps.Select(step => new DtmfPresetStepSetting
            { Kind = step.Kind, Digit = step.Digit, DurationSeconds = step.DurationSeconds }).ToList()
        }).ToList();
        destination.TonePresets = source.TonePresets.Select(preset => new TonePresetSetting
        {
            Name = preset.Name,
            FrequencyHz = preset.FrequencyHz,
            DurationSeconds = preset.DurationSeconds,
            Steps = preset.Steps.Select(step => new TonePresetStepSetting
            { Kind = step.Kind, FrequencyHz = step.FrequencyHz, DurationSeconds = step.DurationSeconds }).ToList()
        }).ToList();
        return destination;
    }
}
