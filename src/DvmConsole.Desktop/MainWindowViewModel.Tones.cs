using Avalonia.Threading;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using System.Globalization;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
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
            return channel.CanTransmit && system?.IsConnected == true && system.SourceId is uint sourceId && sourceId != 0;
        });
    }

    private void SaveDtmfPreset()
    {
        try
        {
            string digits = NormalizeDtmfInput(DtmfDigits);
            string name = string.IsNullOrWhiteSpace(DtmfPresetName)
                ? $"DTMF preset {dtmfPresets.Count + 1}"
                : DtmfPresetName.Trim();
            if (name.Length > 80)
                throw new ArgumentException("Preset names must be 80 characters or fewer.", nameof(DtmfPresetName));

            DtmfPresetViewModel next = new(new DtmfPresetSetting
            {
                Name = name,
                Digits = digits,
                Steps = digits
                    .Select(digit => new DtmfPresetStepSetting
                    {
                        Kind = AudioPresetStepKinds.Digit,
                        Digit = digit.ToString(),
                        DurationSeconds = 0.25
                    })
                    .ToList()
            });
            int existingIndex = dtmfPresets
                .Select((preset, index) => (preset, index))
                .Where(item => item.preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            if (existingIndex >= 0 && existingIndex < dtmfPresets.Count)
                dtmfPresets[existingIndex] = next;
            else
                dtmfPresets.Add(next);

            userSettings.DtmfPresets = dtmfPresets
                .Select(ToDtmfPresetSetting)
                .ToList();
            PersistUserSettings();
            DtmfPresetName = string.Empty;
            TransmitStatusText = $"DTMF preset '{name}' saved.";
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"DTMF preset unavailable: {exception.Message}";
        }
    }


    private void SaveTonePreset()
    {
        if (!TryBuildToneSequence(out GeneratedToneSequence? sequence, out string? error))
        {
            TransmitStatusText = error!;
            return;
        }

        ToneSequenceStepViewModel firstTone = toneSequenceSteps.First(step => !step.IsSilence);
        double frequency = double.Parse(firstTone.FrequencyText, CultureInfo.InvariantCulture);
        double durationSeconds = double.Parse(firstTone.DurationText, CultureInfo.InvariantCulture);

        string name = string.IsNullOrWhiteSpace(TonePresetName)
            ? $"Tone preset {tonePresets.Count + 1}"
            : TonePresetName.Trim();
        if (name.Length > 80)
        {
            TransmitStatusText = "Preset names must be 80 characters or fewer.";
            return;
        }

        TonePresetViewModel next = new(new TonePresetSetting
        {
            Name = name,
            FrequencyHz = frequency,
            DurationSeconds = durationSeconds,
            Steps = toneSequenceSteps.Select(step => new TonePresetStepSetting
            {
                Kind = step.IsSilence ? AudioPresetStepKinds.Hold : AudioPresetStepKinds.Tone,
                FrequencyHz = step.IsSilence
                    ? 0
                    : double.Parse(step.FrequencyText, CultureInfo.InvariantCulture),
                DurationSeconds = double.Parse(step.DurationText, CultureInfo.InvariantCulture)
            }).ToList()
        });
        int existingIndex = tonePresets
            .Select((preset, index) => (preset, index))
            .Where(item => item.preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (existingIndex >= 0 && existingIndex < tonePresets.Count)
            tonePresets[existingIndex] = next;
        else
            tonePresets.Add(next);

        userSettings.TonePresets = tonePresets
            .Select(ToTonePresetSetting)
            .ToList();
        PersistUserSettings();
        TonePresetName = string.Empty;
        TransmitStatusText = $"Tone preset '{name}' saved ({sequence!.Duration.TotalSeconds:0.##} sec).";
    }

    public void UseDtmfPreset(DtmfPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        DtmfDigits = preset.Digits;
        TransmitStatusText = $"DTMF preset '{preset.Name}' loaded.";
    }

    public void DeleteDtmfPreset(DtmfPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!dtmfPresets.Remove(preset))
            return;
        userSettings.DtmfPresets = dtmfPresets
            .Select(ToDtmfPresetSetting)
            .ToList();
        PersistUserSettings();
        TransmitStatusText = $"DTMF preset '{preset.Name}' deleted.";
    }

    public void UseTonePreset(TonePresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        toneSequenceSteps.Clear();
        foreach (TonePresetStepSetting step in preset.Steps)
        {
            bool isSilence = string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase);
            toneSequenceSteps.Add(new ToneSequenceStepViewModel(
                isSilence ? GeneratedToneStep.MinimumSingleToneFrequencyHz : step.FrequencyHz,
                step.DurationSeconds,
                isSilence));
        }
        TransmitStatusText = $"Tone preset '{preset.Name}' loaded.";
    }

    public void AddToneSequenceStep(bool silence)
    {
        toneSequenceSteps.Add(new ToneSequenceStepViewModel(
            silence ? GeneratedToneStep.MinimumSingleToneFrequencyHz : userSettings.ToneFrequencyHz,
            silence ? 0.2 : userSettings.ToneDurationSeconds,
            silence));
    }

    public void RemoveToneSequenceStep(ToneSequenceStepViewModel step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (toneSequenceSteps.Count <= 1)
        {
            TransmitStatusText = "A custom tone pattern must retain at least one step.";
            return;
        }
        toneSequenceSteps.Remove(step);
    }

    public void MoveToneSequenceStep(ToneSequenceStepViewModel step, int offset)
    {
        ArgumentNullException.ThrowIfNull(step);
        int current = toneSequenceSteps.IndexOf(step);
        int next = Math.Clamp(current + offset, 0, toneSequenceSteps.Count - 1);
        if (current >= 0 && next != current)
            toneSequenceSteps.Move(current, next);
    }

    public void DeleteTonePreset(TonePresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!tonePresets.Remove(preset))
            return;
        userSettings.TonePresets = tonePresets
            .Select(ToTonePresetSetting)
            .ToList();
        PersistUserSettings();
        TransmitStatusText = $"Tone preset '{preset.Name}' deleted.";
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
            var sequence = new GeneratedToneSequence(preset.Steps.Select(step =>
                string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase)
                    ? GeneratedToneStep.Silence(TimeSpan.FromSeconds(step.DurationSeconds))
                    : GeneratedToneStep.Tone(step.FrequencyHz, TimeSpan.FromSeconds(step.DurationSeconds))));
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

    public bool AddAlertTone(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("The alert audio file was not found.", fullPath);

            string name = string.IsNullOrWhiteSpace(AlertToneNameText)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : AlertToneNameText.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
                throw new ArgumentException("Alert tone names must contain 1–80 characters.", nameof(path));

            AlertToneViewModel? existing = alertTones.FirstOrDefault(tone =>
                tone.FilePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                alertTones.Remove(existing);
            alertTones.Add(new AlertToneViewModel(new AlertToneSetting
            {
                Name = name,
                FilePath = fullPath
            }));
            userSettings.AlertTones = alertTones.Select(tone => tone.ToSetting()).ToList();
            PersistUserSettings();
            AlertToneNameText = string.Empty;
            TransmitStatusText = $"Alert asset '{name}' imported.";
            return true;
        }
        catch (Exception exception)
        {
            TransmitStatusText = $"Alert asset unavailable: {exception.Message}";
            return false;
        }
    }

    public void DeleteAlertTone(AlertToneViewModel tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        if (!alertTones.Remove(tone))
            return;
        userSettings.AlertTones = alertTones.Select(item => item.ToSetting()).ToList();
        PersistUserSettings();
        TransmitStatusText = $"Alert asset '{tone.Name}' removed.";
    }

    public async Task SendAlertToneAsync(AlertToneViewModel tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        try
        {
            short[] samples = await PcmAudioFileLoader.LoadAsync(tone.FilePath);
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

    public async Task SendBuiltInAlertToneAsync(BuiltInAlertToneViewModel tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
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

    private static DtmfPresetSetting ToDtmfPresetSetting(DtmfPresetViewModel preset)
        => new()
        {
            Name = preset.Name,
            Digits = preset.Digits,
            Steps = preset.Steps
                .Select(step => new DtmfPresetStepSetting
                {
                    Kind = step.Kind,
                    Digit = step.Digit,
                    DurationSeconds = step.DurationSeconds
                })
                .ToList()
        };

    private static TonePresetSetting ToTonePresetSetting(TonePresetViewModel preset)
        => new()
        {
            Name = preset.Name,
            FrequencyHz = preset.FrequencyHz,
            DurationSeconds = preset.DurationSeconds,
            Steps = preset.Steps
                .Select(step => new TonePresetStepSetting
                {
                    Kind = step.Kind,
                    FrequencyHz = step.FrequencyHz,
                    DurationSeconds = step.DurationSeconds
                })
                .ToList()
        };

    private bool TryBuildToneSequence(out GeneratedToneSequence? sequence, out string? error)
    {
        sequence = null;
        error = null;
        var steps = new List<GeneratedToneStep>(toneSequenceSteps.Count);
        bool hasTone = false;
        foreach ((ToneSequenceStepViewModel step, int index) in toneSequenceSteps.Select((step, index) => (step, index)))
        {
            if (!double.TryParse(
                    step.DurationText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double durationSeconds) ||
                durationSeconds <= 0 || durationSeconds > 10)
            {
                error = $"Step {index + 1} duration must be greater than 0 and no more than 10 seconds.";
                return false;
            }

            if (step.IsSilence)
            {
                steps.Add(GeneratedToneStep.Silence(TimeSpan.FromSeconds(durationSeconds)));
                continue;
            }

            if (!double.TryParse(
                    step.FrequencyText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double frequency) ||
                frequency < GeneratedToneStep.MinimumSingleToneFrequencyHz ||
                frequency > GeneratedToneStep.MaximumSingleToneFrequencyHz)
            {
                error = $"Step {index + 1} frequency must be 300–2500 Hz.";
                return false;
            }

            steps.Add(GeneratedToneStep.Tone(frequency, TimeSpan.FromSeconds(durationSeconds)));
            hasTone = true;
        }

        if (!hasTone)
        {
            error = "A custom tone pattern must contain at least one tone step.";
            return false;
        }

        sequence = new GeneratedToneSequence(steps);
        if (sequence.Duration > TimeSpan.FromSeconds(30))
        {
            sequence = null;
            error = "A custom tone pattern cannot exceed 30 seconds.";
            return false;
        }
        return true;
    }

    private static string NormalizeDtmfInput(string value)
    {
        string normalized = new string((value ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character))
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (normalized.Length is 0 or > 64 || normalized.Any(character => !DtmfToneGenerator.IsDigit(character)))
            throw new ArgumentException("DTMF must contain 1–64 digits from 0–9, *, #, or A–D.", nameof(value));
        return normalized;
    }

    private async Task SendGeneratedToneAsync(
        ReadOnlyMemory<short> samples,
        string label,
        IReadOnlyCollection<ChannelViewModel>? explicitTargets = null)
    {
        ChannelViewModel[] channels = explicitTargets?.ToArray() ?? ResolveGeneratedToneChannels();
        if (channels.Length == 0)
            throw new InvalidOperationException("Arm ALERT on one or more channel cards before sending DTMF or alert audio.");

        TransmitTarget[] targets = channels
            .Distinct()
            .Select(channel => new TransmitTarget(
                channel,
                Systems.FirstOrDefault(candidate => candidate.Name.Equals(
                    channel.Definition.SystemName,
                    StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException(
                        $"The system '{channel.Definition.SystemName}' was not found.")))
            .ToArray();
        if (transmitCoordinator.ActiveChannel is not null)
            throw new InvalidOperationException("Release PTT before sending generated audio.");

        await MuteReceiveAudioAsync("RX audio muted while sending generated audio.");

        try
        {
            await toneTransmitCoordinator.SendAsync(targets, samples);
            string targetText = FormatToneTargetText(targets.Select(target => target.Channel));
            await RunOnUiThreadAsync(() => TransmitStatusText = $"{label} sent on {targetText}.");
        }
        finally
        {
            await RestoreSuspendedAudioAsync();
            await RunOnUiThreadAsync(RaiseGeneratedAudioCanExecuteChanged);
        }
    }

    private async Task SendGeneratedToneAsync(
        GeneratedToneSequence sequence,
        string label,
        IReadOnlyCollection<ChannelViewModel>? explicitTargets = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ChannelViewModel[] channels = explicitTargets?.ToArray() ?? ResolveGeneratedToneChannels();
        if (channels.Length == 0)
            throw new InvalidOperationException("Arm ALERT on one or more channel cards before sending DTMF or alert audio.");

        TransmitTarget[] targets = channels
            .Distinct()
            .Select(channel => new TransmitTarget(
                channel,
                Systems.FirstOrDefault(candidate => candidate.Name.Equals(
                    channel.Definition.SystemName,
                    StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException(
                        $"The system '{channel.Definition.SystemName}' was not found.")))
            .ToArray();
        if (transmitCoordinator.ActiveChannel is not null)
            throw new InvalidOperationException("Release PTT before sending generated audio.");

        await MuteReceiveAudioAsync("RX audio muted while sending generated audio.");

        try
        {
            short[] monitorSamples = sequence.RenderPcm();
            Exception? monitorFailure = await GeneratedAudioMonitorSession.RunAsync(
                cancellationToken => generatedAudioMonitor.PlayAsync(
                    monitorSamples,
                    cancellationToken),
                () => toneTransmitCoordinator.SendAsync(
                    targets,
                    sequence,
                    monitorSamples));
            string targetText = FormatToneTargetText(targets.Select(target => target.Channel));
            string monitorStatus = monitorFailure is null
                ? string.Empty
                : $" Local monitor unavailable: {monitorFailure.Message}";
            await RunOnUiThreadAsync(() =>
                TransmitStatusText = $"{label} sent on {targetText}.{monitorStatus}");
        }
        finally
        {
            await RestoreSuspendedAudioAsync();
            await RunOnUiThreadAsync(RaiseGeneratedAudioCanExecuteChanged);
        }
    }

    private async Task RunOnUiThreadAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (uiDispatcher.CheckAccess())
        {
            action();
            return;
        }

        await uiDispatcher.InvokeAsync(action);
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
}
