// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed partial class ConsoleOperationalRuntime
{
    private IConsoleCommands? commands;
    public IConsoleCommands Commands
    {
        get => commands ?? throw new InvalidOperationException("Initialize Commands before using this runtime component.");
        private set => commands = value;
    }
    private ChannelTransmitControlCommands? transmitControls;
    public ChannelTransmitControlCommands TransmitControls
    {
        get => transmitControls ?? throw new InvalidOperationException("Initialize TransmitControls before using this runtime component.");
        private set => transmitControls = value;
    }
    private ChannelRecordingCommands? recordingControls;
    public ChannelRecordingCommands RecordingControls
    {
        get => recordingControls ?? throw new InvalidOperationException("Initialize RecordingControls before using this runtime component.");
        private set => recordingControls = value;
    }
    private ChannelAudioSettingsController? audioSettings;
    public ChannelAudioSettingsController AudioSettings
    {
        get => audioSettings ?? throw new InvalidOperationException("Initialize AudioSettings before using this runtime component.");
        private set => audioSettings = value;
    }

    public void InitializeTransmitControls(Func<ChannelId, bool> canSelect,
        Func<ChannelId, ChannelTransmitPreferenceChange, CancellationToken, ValueTask> save, Func<bool> isStopping,
        Action<ChannelSelectionResult>? selectionChanged = null)
    {
        if (transmitControls is not null) throw new InvalidOperationException("Transmit controls are already initialized.");
        TransmitControls = new(TransmitChannels, canSelect, save, isStopping, selectionChanged);
        Commands = new ConsoleChannelCommands(this, isStopping);
    }

    public void InitializeRecordingControls(Func<ChannelId, bool> canRecord,
        Func<ChannelId, bool, CancellationToken, ValueTask> save, Action<Action> applyState,
        Func<ChannelId, bool, CancellationToken, Task> reconcile, Func<bool> isStopping, Action<ChannelId>? changed = null)
    {
        if (recordingControls is not null) throw new InvalidOperationException("Recording controls are already initialized.");
        RecordingControls = new(Media.State, canRecord, save, applyState, id =>
        {
            Recording.Targets.Refresh();
            changed?.Invoke(id);
        }, reconcile, isStopping);
    }

    public void InitializeAudioSettings(
        Func<ChannelId, ChannelReceivePreferenceChange, CancellationToken, ValueTask> save,
        Func<bool> isStopping, Action<ChannelId>? changed = null)
    {
        if (audioSettings is not null) throw new InvalidOperationException("Channel audio settings are already initialized.");
        if (Receive.Audio is null) throw new InvalidOperationException("Initialize receive audio before its controls.");
        AudioSettings = new(id => Channels[id].Operator, save,
            Receive.Audio.SetGainAsync, Receive.Audio.SetBalanceAsync, isStopping, changed);
    }
}
