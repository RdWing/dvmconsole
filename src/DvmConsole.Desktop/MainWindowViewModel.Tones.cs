// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Threading;
using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using System.Globalization;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : ITonePresentationSession
{
    void ITonePresentationSession.BeginUndoableAction(
        string message,
        Func<ValueTask> undo,
        Func<ValueTask>? commit)
        => BeginUndoableAction(message, undo, commit);

    private bool CanSendGeneratedAudio()
    {
        if (busy || toneTransmitCoordinator.IsSending || transmitCoordinator.ActiveChannel is not null)
            return false;

        ChannelViewModel[] targets = ResolveGeneratedToneChannels();
        return targets.Length > 0 && targets.All(channel =>
        {
            SystemViewModel? system = Systems.FirstOrDefault(candidate => candidate.Name.Equals(
                channel.Definition.SystemName,
                StringComparison.OrdinalIgnoreCase));
            return system is not null &&
                TransmitTargetPolicy.IsAvailable(channel.ToTransmitDescriptor(), system) &&
                system.IsConnected &&
                system.SourceId is uint sourceId &&
                sourceId != 0;
        });
    }

    private void SaveDtmfPreset() => tonePresentation.SaveDtmfPreset();


    private void SaveTonePreset() => tonePresentation.SaveTonePreset();

    public void UseDtmfPreset(DtmfPresetViewModel preset)
    {
        tonePresentation.LoadDtmfPreset(preset);
    }

    public void DeleteDtmfPreset(DtmfPresetViewModel preset)
    {
        tonePresentation.DeleteDtmfPreset(preset);
    }

    public void UseTonePreset(TonePresetViewModel preset)
    {
        tonePresentation.LoadTonePreset(preset);
    }

    public void AddToneSequenceStep(bool silence)
    {
        tonePresentation.AddToneSequenceStep(silence);
    }

    public void RemoveToneSequenceStep(ToneSequenceStepViewModel step)
    {
        tonePresentation.RemoveToneSequenceStep(step);
    }

    public void MoveToneSequenceStep(ToneSequenceStepViewModel step, int offset)
    {
        tonePresentation.MoveToneSequenceStep(step, offset);
    }

    public void DeleteTonePreset(TonePresetViewModel preset)
    {
        tonePresentation.DeleteTonePreset(preset);
    }

    private async Task SendDtmfAsync()
    {
        try
        {
            string normalizedDigits = NormalizeDtmfInput(DtmfDigits);
            List<GeneratedToneStep> steps = [];
            foreach (char digit in normalizedDigits)
            {
                if (steps.Count > 0)
                    steps.Add(GeneratedToneStep.Silence(TimeSpan.FromMilliseconds(60)));
                steps.Add(GeneratedToneStep.Dtmf(digit, TimeSpan.FromMilliseconds(240)));
            }
            userSettings.LastDtmfDigits = normalizedDigits;
            PersistUserSettings();
            await SendGeneratedToneAsync(new GeneratedToneSequence(steps), "DTMF");
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"DTMF unavailable: {exception.Message}";
        }
    }

    public async Task SendDtmfPresetAsync(DtmfPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        try
        {
            var sequence = new GeneratedToneSequence(preset.Steps.Select(step =>
                string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase)
                    ? GeneratedToneStep.Silence(TimeSpan.FromSeconds(step.DurationSeconds))
                    : GeneratedToneStep.Dtmf(
                        string.IsNullOrWhiteSpace(step.Digit) ? '1' : step.Digit[0],
                        TimeSpan.FromSeconds(step.DurationSeconds))));
            await SendGeneratedToneAsync(sequence, $"DTMF preset '{preset.Name}'");
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"DTMF preset unavailable: {exception.Message}";
        }
    }

    private async Task SendToneAsync()
    {
        if (!TryBuildToneSequence(out GeneratedToneSequence? sequence, out string? error))
        {
            TransmitStatusText = error!;
            return;
        }

        try
        {
            ToneSequenceStepViewModel firstTone = toneSequenceSteps.First(step => !step.IsSilence);
            double frequency = double.Parse(firstTone.FrequencyText, CultureInfo.InvariantCulture);
            double durationSeconds = double.Parse(firstTone.DurationText, CultureInfo.InvariantCulture);
            userSettings.ToneFrequencyHz = frequency;
            userSettings.ToneDurationSeconds = durationSeconds;
            PersistUserSettings();
            await SendGeneratedToneAsync(sequence!, "Alert tone pattern");
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"Alert tone unavailable: {exception.Message}";
        }
    }

    public async Task SendTonePresetAsync(TonePresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        try
        {
            var sequence = CreatePresetSequence(preset);
            await SendGeneratedToneAsync(sequence, $"Tone preset '{preset.Name}'");
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"Tone preset unavailable: {exception.Message}";
        }
    }

    public async Task SendQuickCallAsync()
    {
        if (!QuickCallToneGenerator.TryParse(
                QuickCallToneAText,
                QuickCallToneBText,
                out double toneAFrequencyHz,
                out double toneBFrequencyHz,
                out string? error))
        {
            TransmitStatusText = error!;
            return;
        }

        ChannelViewModel[] pageTargets = ResolvePageToneChannels();
        if (pageTargets.Length == 0)
        {
            TransmitStatusText = "Arm PAGE on one or more channel cards before sending QCII.";
            return;
        }

        try
        {
            GeneratedToneSequence sequence = QuickCallToneGenerator.CreateSequence(toneAFrequencyHz, toneBFrequencyHz);
            userSettings.QuickCallToneAFrequencyHz = toneAFrequencyHz;
            userSettings.QuickCallToneBFrequencyHz = toneBFrequencyHz;
            PersistUserSettings();
            await SendGeneratedToneAsync(sequence, "QCII page", pageTargets);
            foreach (ChannelViewModel channel in pageTargets)
                channel.SetPageSelected(false);
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"QCII page unavailable: {exception.Message}";
        }
    }

    public async Task<bool> AddAlertToneAsync(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("The alert audio file was not found.", fullPath);

            await using FileStream source = File.OpenRead(fullPath);
            return await AddAlertToneAsync(
                Path.GetFileName(fullPath),
                ResolveAlertMediaType(fullPath),
                source);
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"Alert asset unavailable: {exception.Message}";
            return false;
        }
    }

    public async Task<bool> AddAlertToneAsync(
        string displayName,
        string mediaType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentNullException.ThrowIfNull(content);
        AssetDescriptor? imported = null;
        AlertToneViewModel? addedTone = null;
        try
        {
            string name = string.IsNullOrWhiteSpace(AlertToneNameText)
                ? Path.GetFileNameWithoutExtension(displayName)
                : AlertToneNameText.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
                throw new ArgumentException("Alert tone names must contain 1–80 characters.", nameof(displayName));

            imported = await assetStore.ImportAsync(
                displayName,
                mediaType,
                content,
                cancellationToken);
            addedTone = new AlertToneViewModel(new AlertToneSetting
            {
                Name = name,
                AssetId = imported.Id.ToString(),
                FileName = Path.GetFileName(displayName)
            });
            alertTones.Add(addedTone);
            userSettings.AlertTones = alertTones.Select(tone => tone.ToSetting()).ToList();
            try
            {
                PersistUserSettings();
            }
            catch
            {
                alertTones.Remove(addedTone);
                userSettings.AlertTones = alertTones.Select(tone => tone.ToSetting()).ToList();
                throw;
            }
            AlertToneNameText = string.Empty;
            TransmitStatusText = $"Alert asset '{name}' imported.";
            return true;
        }
        catch (Exception exception)
        {
            if (imported is not null && (addedTone is null || !alertTones.Contains(addedTone)))
            {
                try
                {
                    await assetStore.DeleteIfUnreferencedAsync(imported.Id, [], CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    AddDebugLog(
                        DateTimeOffset.Now,
                        "Assets",
                        DebugLogSeverity.Warning,
                        $"Could not roll back alert asset {imported.Id}: {cleanupException.Message}");
                }
            }
            TransmitStatusText = $"Alert asset unavailable: {exception.Message}";
            return false;
        }
    }

    public void DeleteAlertTone(AlertToneViewModel tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        int index = alertTones.IndexOf(tone);
        if (index < 0)
            return;
        string? removedAssetId = tone.AssetId;
        alertTones.RemoveAt(index);
        userSettings.AlertTones = alertTones.Select(item => item.ToSetting()).ToList();
        PersistUserSettings();
        BeginUndoableAction(
            $"Alert asset '{tone.Name}' deleted.",
            () =>
            {
                alertTones.Insert(Math.Min(index, alertTones.Count), tone);
                userSettings.AlertTones = alertTones.Select(item => item.ToSetting()).ToList();
                PersistUserSettings();
                TransmitStatusText = $"Alert asset '{tone.Name}' restored.";
                return ValueTask.CompletedTask;
            },
            () => DeleteAssetIfUnreferencedAsync(removedAssetId));
        TransmitStatusText = $"Alert asset '{tone.Name}' deleted. Undo is available for 8 seconds.";
    }

    public async Task SendAlertToneAsync(AlertToneViewModel tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        try
        {
            Stream source = await OpenAlertToneAsync(tone);
            short[] samples = await PcmAudioFileLoader.LoadAsync(source);
            ChannelViewModel[] alertTargets = ResolveGeneratedToneChannels();
            await SendGeneratedToneAsync(
                samples,
                $"Alert asset '{tone.Name}'",
                alertTargets);
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"Alert asset unavailable: {exception.Message}";
        }
    }

    private async ValueTask<Stream> OpenAlertToneAsync(AlertToneViewModel tone)
    {
        if (Guid.TryParse(tone.AssetId, out Guid managedId))
            return await assetStore.OpenReadAsync(new AssetId(managedId));

        if (string.IsNullOrWhiteSpace(tone.FilePath) || !File.Exists(tone.FilePath))
            throw new FileNotFoundException("The alert audio asset is not available.", tone.FilePath);

        // One-time migration for pre-library desktop settings. The original
        // remains untouched and the next settings write omits its path.
        await using FileStream source = File.OpenRead(tone.FilePath);
        AssetDescriptor imported = await assetStore.ImportAsync(
            tone.FileName,
            ResolveAlertMediaType(tone.FilePath),
            source);
        BuiltInAlertToneViewModel[] migratedShortcuts = BuiltInAlertTones
            .Where(button => ReferenceEquals(ResolveToolbarCustomAlert(button), tone))
            .ToArray();
        tone.SetManagedAsset(imported.Id);
        foreach (BuiltInAlertToneViewModel button in migratedShortcuts)
            SetToolbarToneAssignment(button, CreateCustomAlertAssignment(tone), persist: false);
        userSettings.AlertTones = alertTones.Select(item => item.ToSetting()).ToList();
        PersistUserSettings();
        return await assetStore.OpenReadAsync(imported.Id);
    }

    internal static string ResolveAlertMediaType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".mp3" or ".mp2" or ".mpeg" => "audio/mpeg",
            ".opus" => "audio/opus",
            ".ogg" => "audio/ogg",
            _ => "application/octet-stream"
        };

    public void AssignToolbarTone(BuiltInAlertToneViewModel button, TonePresetViewModel? preset)
    {
        if (preset is not null && !TonePresets.Contains(preset))
            throw new ArgumentException("The pattern is no longer saved.", nameof(preset));
        SetToolbarToneAssignment(button, preset is null ? null : new()
        {
            PresetName = preset.Name
        });
    }

    public void AssignToolbarCustomAlert(BuiltInAlertToneViewModel button, AlertToneViewModel alert)
    {
        if (!AlertTones.Contains(alert))
            throw new ArgumentException("The custom alert is no longer saved.", nameof(alert));
        SetToolbarToneAssignment(button, CreateCustomAlertAssignment(alert));
    }

    private static ToolbarToneAssignmentSetting CreateCustomAlertAssignment(AlertToneViewModel alert)
        => new()
        {
            PresetName = alert.Name,
            IsCustomAudio = true,
            AssetId = alert.AssetId,
            FilePath = string.IsNullOrWhiteSpace(alert.AssetId) ? alert.FilePath : null
        };

    internal AlertToneViewModel? ResolveToolbarCustomAlert(BuiltInAlertToneViewModel button)
        => button.IsCustomAudio
            ? ToolbarCustomAlertResolver.Resolve(AlertTones, button.AssignedPresetName,
                button.AssignedAssetId, button.AssignedFilePath)
            : null;

    private void SetToolbarToneAssignment(
        BuiltInAlertToneViewModel button, ToolbarToneAssignmentSetting? assignment, bool persist = true)
    {
        if (!BuiltInAlertTones.Contains(button))
            throw new ArgumentException("Unknown toolbar tone button.", nameof(button));
        if (assignment is null)
            userSettings.ToolbarToneAssignments.Remove((int)button.Tone);
        else
            userSettings.ToolbarToneAssignments[(int)button.Tone] = assignment;
        button.Assign(assignment);
        if (persist)
            PersistUserSettings();
    }

    public async Task SendBuiltInAlertToneAsync(BuiltInAlertToneViewModel tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        if (tone.AssignedPresetName is string presetName)
        {
            if (tone.IsCustomAudio)
            {
                AlertToneViewModel? alert = ResolveToolbarCustomAlert(tone);
                if (alert is null)
                    TransmitStatusText = $"Custom alert '{presetName}' is unavailable or ambiguous. Right-click the tone button to assign another alert.";
                else
                    await SendAlertToneAsync(alert);
                return;
            }
            TonePresetViewModel? preset = TonePresets.FirstOrDefault(candidate =>
                candidate.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (preset is null)
            {
                TransmitStatusText = $"Saved pattern '{presetName}' is unavailable. Right-click the tone button to assign another pattern.";
                return;
            }
            await SendTonePresetAsync(preset);
            return;
        }
        try
        {
            await SendGeneratedToneAsync(
                tone.CreateSequence(),
                tone.Name,
                ResolveGeneratedToneChannels());
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"{tone.Name} unavailable: {exception.Message}";
        }
    }

    private static GeneratedToneSequence CreatePresetSequence(TonePresetViewModel preset)
        => new(preset.Steps.Select(step =>
            string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase)
                ? GeneratedToneStep.Silence(TimeSpan.FromSeconds(step.DurationSeconds))
                : GeneratedToneStep.Tone(step.FrequencyHz, TimeSpan.FromSeconds(step.DurationSeconds))));

    private bool TryBuildToneSequence(out GeneratedToneSequence? sequence, out string? error)
        => tonePresentation.TryBuildToneSequence(out sequence, out error);

    private static string NormalizeDtmfInput(string value)
        => TonePresentationController.NormalizeDtmfInput(value);

    private Task SendGeneratedToneAsync(
        ReadOnlyMemory<short> samples,
        string label,
        IReadOnlyCollection<ChannelViewModel>? explicitTargets = null)
        => SendGeneratedAudioAsync(label, explicitTargets,
            targets => generatedAudioOperation.SendAsync(targets, samples));

    private Task SendGeneratedToneAsync(
        GeneratedToneSequence sequence,
        string label,
        IReadOnlyCollection<ChannelViewModel>? explicitTargets = null)
        => SendGeneratedAudioAsync(label, explicitTargets,
            targets => generatedAudioOperation.SendAsync(targets, sequence));

    private async Task SendGeneratedAudioAsync(
        string label,
        IReadOnlyCollection<ChannelViewModel>? explicitTargets,
        Func<TransmitTarget[], Task<Exception?>> send)
    {
        ChannelViewModel[] channels = explicitTargets?.ToArray() ?? ResolveGeneratedToneChannels();
        if (channels.Length == 0)
            throw new InvalidOperationException("Arm ALERT on one or more channel cards before sending DTMF or alert audio.");
        TransmitTarget[] targets = channels.Distinct().Select(channel => new TransmitTarget(
            channel.ToTransmitDescriptor(),
            Systems.FirstOrDefault(system => system.Name.Equals(
                channel.Definition.SystemName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The system '{channel.Definition.SystemName}' was not found.")))
            .ToArray();
        try
        {
            Exception? monitorFailure = await send(targets);
            string monitorStatus = monitorFailure is null
                ? string.Empty : $" Local monitor unavailable: {monitorFailure.Message}";
            await RunOnUiThreadAsync(() =>
                TransmitStatusText = $"{label} sent on {FormatToneTargetText(channels)}.{monitorStatus}");
        }
        finally
        {
            await RunOnUiThreadAsync(RaiseGeneratedAudioCanExecuteChanged);
        }
    }

    private async Task RunOnUiThreadAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (terminalFence.IsClosed)
            return;
        if (uiDispatcher.CheckAccess())
        {
            terminalFence.TryRun(action);
            return;
        }

        await uiDispatcher.InvokeAsync(() => terminalFence.TryRun(action));
    }

    private void PostToUi(Action action, bool background = false)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (terminalFence.IsClosed)
            return;
        uiDispatcher.Post(() => terminalFence.TryRun(action), background);
    }

    internal ChannelViewModel[] ResolveGeneratedToneChannels()
        => Systems
            .SelectMany(system => system.Channels)
            .Where(channel => channel.IsAlertSelected)
            .Distinct()
            .ToArray();

    internal ChannelViewModel[] ResolvePageToneChannels()
        => Systems
            .SelectMany(system => system.Channels)
            .Where(channel => channel.IsPageSelected)
            .Distinct()
            .ToArray();

    private static string FormatToneTargetText(IEnumerable<ChannelViewModel> channels)
    {
        string[] names = channels.Select(channel => channel.Name).Distinct().ToArray();
        return names.Length <= 4
            ? string.Join(", ", names)
            : $"{names.Length} ALERT/PAGE-selected channels";
    }

    private void RaiseGeneratedAudioCanExecuteChanged()
    {
        (SendDtmfCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SendToneCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static FneTrafficProtocol ProtocolFor(ChannelViewModel channel)
        => FneTrafficProtocolMapper.FromChannelProtocol(channel.Definition.Protocol);

    void ITonePresentationSession.PersistUserSettings() => PersistUserSettings();

    void ITonePresentationSession.SetTransmitStatus(string status)
        => TransmitStatusText = status;
}
