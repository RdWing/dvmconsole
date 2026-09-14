// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media;
using DvmConsole.Application;
using DvmConsole.Threading;

namespace DvmConsole.Presentation;

/// <summary>Card presentation of the same snapshot and commands used by List.</summary>
public sealed class ChannelSnapshotCardViewModel : IChannelCardViewModel, INotifyPropertyChanged, IDisposable
{
    private readonly ChannelListItemViewModel item;
    private readonly IConsoleRecordingCommands? recordings;
    private readonly ActionCommand encryptionCommand;
    private readonly ActionCommand recordingCommand;
    private bool dark;
    private readonly bool touch;
    private bool disposed;

    public ChannelSnapshotCardViewModel(ChannelListItemViewModel item, IConsoleRecordingCommands? recordings = null, bool useTouchPalette = false)
    {
        touch = useTouchPalette;
        this.item = item ?? throw new ArgumentNullException(nameof(item));
        item.UseTouchText = useTouchPalette;
        this.recordings = recordings;
        encryptionCommand = new ActionCommand(() => CanToggleEncryption, ToggleEncryptionAsync);
        recordingCommand = new ActionCommand(() => CanRecord, ToggleRecordingAsync);
        item.SnapshotApplied += OnSnapshotApplied;
        item.PropertyChanged += OnItemPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string? CommandError { get; private set; }
    public ChannelId Id => item.Id;
    public string Name => item.Name;
    public string TalkgroupText => $"{item.TalkgroupText} · {item.ProtocolText}";
    public string LastCallerDisplayText => item.LastCallerText == "—" ? "Last: —" : item.LastCallerText;
    private ConsoleCardActivity Activity => item.Snapshot.TransmitStarting ? ConsoleCardActivity.TransmitStarting
        : item.IsTransmitting ? ConsoleCardActivity.Transmitting
        : item.ReceiveActive ? ConsoleCardActivity.Receiving
        : item.ReceiveEnabled ? ConsoleCardActivity.Listening : ConsoleCardActivity.Idle;
    public IBrush CardBackgroundBrush => ConsoleCardPalette.Background(dark, Activity, touch);
    public IBrush CardBorderBrush => ConsoleCardPalette.Border(dark, Activity, touch);
    public IBrush CardTextBrush => ConsoleCardPalette.Text(dark, Activity, touch);
    public double CardWidth => ConsoleCardGeometry.ResolveWidth(item.Descriptor.CardSize);
    public AudioMeterState AudioMeter => item.Meter;
    public double AudioMeterWidth => ConsoleCardGeometry.MeterWidth(CardWidth);
    public double VolumeSliderValue
    {
        get => item.VolumeSliderValue;
        set
        {
            if (!disposed && Math.Abs(value - item.VolumeSliderValue) > 0.000001)
                TaskObservation.Observe(RunAsync(() => item.SetVolumeSliderValueAsync(value)));
        }
    }
    public string VolumeAutomationName => $"{Name} volume";
    public string EncryptionButtonText => item.IsTransmitEncrypted ? "SECURE" : "CLEAR";
    public string EncryptionAutomationName => $"{Name} transmit encryption";
    public string EncryptionAutomationHelpText => item.TransmitEncryptionText;
    public IBrush EncryptionSelectionBrush => Selection(ConsoleCardSelection.Encryption, item.IsTransmitEncrypted);
    public IBrush EncryptionSelectionBorderBrush => Selection(ConsoleCardSelection.Encryption, item.IsTransmitEncrypted, true);
    public IBrush EncryptionSelectionTextBrush => ConsoleCardPalette.EncryptionText(dark, item.IsTransmitEncrypted);
    public ICommand EncryptionCommand => encryptionCommand;
    public bool CanToggleEncryption => !disposed && item.CanToggleEncryption;
    public string PttButtonText => item.PttText;
    public string PttAutomationName => $"{Name} push to talk";
    public string PttAutomationHelpText => CommandError ?? (IsPttControlEnabled ? "Push to talk." : "Transmit unavailable.");
    public bool IsPttControlEnabled => !disposed && item.IsPttEnabled;
    public bool IsTransmitSelected => item.IsTransmitSelected;
    public string TransmitSelectionText => "TX";
    public string TransmitSelectionAutomationName => $"{Name} multi-select transmit";
    public IBrush TransmitSelectionBrush => Selection(ConsoleCardSelection.Transmit, IsTransmitSelected);
    public IBrush TransmitSelectionBorderBrush => Selection(ConsoleCardSelection.Transmit, IsTransmitSelected, true);
    public bool CanTransmit => !disposed && item.CanSelectTransmitTargets;
    public bool IsPageSelected => item.IsPageSelected;
    public string PageSelectionText => "PAGE";
    public string PageSelectionAutomationName => $"{Name} paging target";
    public IBrush PageSelectionBrush => Selection(ConsoleCardSelection.Page, IsPageSelected);
    public IBrush PageSelectionBorderBrush => Selection(ConsoleCardSelection.Page, IsPageSelected, true);
    public bool IsAlertSelected => item.IsAlertSelected;
    public string AlertSelectionText => "ALERT";
    public string AlertSelectionAutomationName => $"{Name} alert target";
    public IBrush AlertSelectionBrush => Selection(ConsoleCardSelection.Alert, IsAlertSelected);
    public IBrush AlertSelectionBorderBrush => Selection(ConsoleCardSelection.Alert, IsAlertSelected, true);
    public bool IsRecordingEnabled => item.Snapshot.TarArmed;
    public string RecordButtonText => "TAR";
    public string RecordingAutomationName => $"{Name} Talkgroup Audio Recording";
    public string RecordingAutomationHelpText => CommandError ?? item.TarText;
    public IBrush RecordingSelectionBrush => Selection(ConsoleCardSelection.Recording, IsRecordingEnabled);
    public IBrush RecordingSelectionBorderBrush => Selection(ConsoleCardSelection.Recording, IsRecordingEnabled, true);
    public ICommand RecordingCommand => recordingCommand;
    public bool CanRecord => !disposed && recordings is not null && recordings.CanRecord(Id);

    public Task ToggleTransmitSelectionAsync() => RunAsync(() => item.ToggleTransmitSelectionAsync());
    public Task TogglePageSelectionAsync() => RunAsync(() => item.TogglePageSelectionAsync());
    public Task ToggleAlertSelectionAsync() => RunAsync(() => item.ToggleAlertSelectionAsync());
    public Task ToggleEncryptionAsync() => CanToggleEncryption
        ? RunAsync(() => item.ToggleTransmitEncryptionAsync()) : Task.CompletedTask;
    public Task ToggleRecordingAsync() => CanRecord
        ? RunAsync(() => recordings!.SetRecordingEnabledAsync(Id, !IsRecordingEnabled)) : Task.CompletedTask;

    public void SetDarkMode(bool value)
    {
        if (dark == value || disposed) return;
        dark = value;
        Changed();
    }
    private IBrush Selection(ConsoleCardSelection kind, bool selected, bool border = false)
        => ConsoleCardPalette.Selection(dark, kind, selected, border, touch);
    private async Task RunAsync(Func<ValueTask> action)
    {
        if (disposed) return;
        try { await action(); CommandError = null; }
        catch (Exception exception) { CommandError = exception.Message; }
        if (!disposed) Changed();
    }
    private void OnSnapshotApplied(object? sender, EventArgs args) => Changed();
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ChannelListItemViewModel.Meter))
            PropertyChanged?.Invoke(this, new(nameof(AudioMeter)));
    }
    private void Changed()
    {
        PropertyChanged?.Invoke(this, new(null));
        encryptionCommand.Refresh();
        recordingCommand.Refresh();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        item.SnapshotApplied -= OnSnapshotApplied;
        item.PropertyChanged -= OnItemPropertyChanged;
        Changed();
    }
    private sealed class ActionCommand(Func<bool> canExecute, Func<Task> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public void Execute(object? parameter)
        {
            if (CanExecute(parameter)) TaskObservation.Observe(execute());
        }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
