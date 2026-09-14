// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using DvmConsole.Presentation;

namespace DvmConsole.Mobile;

/// <summary>Mobile command/persistence adapter for the shared tone editor.</summary>
internal sealed class MobileToneViewModel : IToneSettingsViewModel, ITonePresentationSession, INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private readonly ToneWorkspaceViewModel workspace;
    private readonly UserSettings settings;
    private readonly IConsoleToneSettingsStore store;
    private readonly IConsoleApplicationSession session;
    private readonly IAssetStore? assets;
    private readonly OperatorUndoController undo;
    private readonly HashSet<AssetId> pendingAssetCleanup = [];
    private AssetId? undoAsset;
    private Task? retirement;
    private bool dirty;
    private string pendingStatus = "";
    private bool disposed;
    private Task activeOperation = Task.CompletedTask;
    private CancellationTokenSource? operationCancellation;
    private CancellationToken OperationToken => operationCancellation?.Token ?? CancellationToken.None;
    public TonePresentationController Editor { get; }
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "";
    public bool CanUndo => undo.CanUndo;
    public bool HasUnsavedChanges => dirty;
    public bool HasPendingAssetCleanup => pendingAssetCleanup.Any(id => id != undoAsset);
    public bool LocalMonitorEnabled => settings.LocalToneMonitorEnabled;
    public event PropertyChangedEventHandler? PropertyChanged;

    public MobileToneViewModel(UserSettings settings, IConsoleToneSettingsStore store, IConsoleApplicationSession session, IAssetStore? assets = null)
    {
        this.settings = settings;
        this.store = store;
        this.session = session;
        this.assets = assets;
        undo = new(action => Dispatcher.UIThread.Post(action), exception =>
            Dispatcher.UIThread.Post(() =>
            {
                if (disposed) return;
                Status = exception.Message;
                Notify(nameof(Status));
            }));
        undo.Changed += UndoChanged;
        workspace = new(settings, new ManagedOnlyFiles());
        workspace.PropertyChanged += WorkspaceChanged;
        Editor = new(workspace, settings, this);
        SendDtmfCommand = new Command(() => SendDtmfAsync());
        SaveDtmfPresetCommand = new Command(() => EditAsync(Editor.SaveDtmfPreset));
        SendToneCommand = new Command(SendPatternAsync);
        SaveTonePresetCommand = new Command(() => EditAsync(Editor.SaveTonePreset));
        session.SnapshotChanged += SnapshotChanged;
        RefreshTargets();
    }
    public ICommand SendDtmfCommand { get; }
    public ICommand SaveDtmfPresetCommand { get; }
    public ICommand SendToneCommand { get; }
    public ICommand SaveTonePresetCommand { get; }
    public string AlertTargetSummary => workspace.AlertTargetSummary;
    public string AlertTargetDetails => workspace.AlertTargetDetails;
    public string PageTargetSummary => workspace.PageTargetSummary;
    public string PageTargetDetails => workspace.PageTargetDetails;
    public string DtmfDigits { get => workspace.DtmfDigits; set => workspace.DtmfDigits = value; }
    public string DtmfPresetName { get => workspace.DtmfPresetName; set => workspace.DtmfPresetName = value; }
    public string DtmfPresetFilterText { get => workspace.DtmfPresetFilterText; set => workspace.DtmfPresetFilterText = value; }
    public string TonePresetName { get => workspace.TonePresetName; set => workspace.TonePresetName = value; }
    public string TonePresetFilterText { get => workspace.TonePresetFilterText; set => workspace.TonePresetFilterText = value; }
    public string QuickCallToneAText { get => workspace.QuickCallToneAText; set => workspace.QuickCallToneAText = value; }
    public string QuickCallToneBText { get => workspace.QuickCallToneBText; set => workspace.QuickCallToneBText = value; }
    public string AlertToneNameText { get => workspace.AlertToneNameText; set => workspace.AlertToneNameText = value; }
    public string AlertToneFilterText { get => workspace.AlertToneFilterText; set => workspace.AlertToneFilterText = value; }
    public IEnumerable DtmfPresets => workspace.DtmfPresets;
    public IEnumerable FilteredDtmfPresets => workspace.FilteredDtmfPresets;
    public IEnumerable ToneSequenceSteps => workspace.ToneSequenceSteps;
    public IEnumerable TonePresets => workspace.TonePresets;
    public IEnumerable FilteredTonePresets => workspace.FilteredTonePresets;
    public IEnumerable AlertTones => workspace.AlertTones;
    public IEnumerable FilteredAlertTones => workspace.FilteredAlertTones;
    public bool IsDtmfPresetFilterVisible => workspace.IsDtmfPresetFilterVisible;
    public bool IsTonePresetFilterVisible => workspace.IsTonePresetFilterVisible;
    public bool IsAlertToneFilterVisible => workspace.IsAlertToneFilterVisible;

    public Task EditAsync(Action action) => RunAsync(() => { action(); return Task.CompletedTask; });
    public Task UndoAsync() => RunAsync(async () =>
    {
        await undo.UndoAsync();
    });
    private Task RunAsync(Func<Task> action)
    {
        if (IsBusy || disposed) return Task.CompletedTask;
        return activeOperation = RunCoreAsync(action);
    }
    private async Task RunCoreAsync(Func<Task> action)
    {
        using var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        IsBusy = true;
        pendingStatus = "";
        Notify(nameof(IsBusy));
        try
        {
            await action();
            await SavePendingAsync();
            await CleanUpAssetsAsync();
            Status = pendingStatus;
        }
        catch (OperationCanceledException) { Status = "Operation cancelled."; }
        catch (Exception exception) { Status = exception.Message + (dirty ? " Changes have not been saved." : ""); }
        finally { operationCancellation = null; IsBusy = false; Notify(nameof(IsBusy)); Notify(nameof(Status)); }
    }
    public Task RetrySaveAsync() => RunAsync(() => Task.CompletedTask);
    private ValueTask QueueAssetCleanupAsync(string? value)
    {
        if (assets is null || store is not IConsoleToneAssetMaintenance || !Guid.TryParse(value, out Guid id))
            return ValueTask.CompletedTask;
        pendingAssetCleanup.Add(new(id));
        Notify(nameof(HasPendingAssetCleanup));
        // A replacement undo action may commit while its settings save is in
        // progress. That operation drains cleanup only after a successful save.
        if (IsBusy) return ValueTask.CompletedTask;
        return new(disposed ? CleanUpAssetsAsync() : RunAsync(() => Task.CompletedTask));
    }
    private async Task CleanUpAssetsAsync()
    {
        if (dirty || pendingAssetCleanup.Count == 0 || assets is null || store is not IConsoleToneAssetMaintenance maintenance) return;
        var references = ConsoleAssetReferences.FromSettings(settings);
        foreach (AssetId id in pendingAssetCleanup.ToArray())
        {
            if (id == undoAsset) continue;
            if (!references.Contains(id))
                await maintenance.DeleteUnreferencedToneAssetAsync(id, assets);
            pendingAssetCleanup.Remove(id);
        }
        Notify(nameof(HasPendingAssetCleanup));
    }
    private async Task SavePendingAsync()
    {
        if (!dirty) return;
        await store.SaveToneSettingsAsync(settings, CancellationToken.None);
        dirty = false;
        if (session.Commands is IConsoleToneCommands commands) commands.LocalToneMonitorEnabled = settings.LocalToneMonitorEnabled;
    }
    public string MainAlertPresetName => settings.MobileAlertPresetName;
    public int? MainAlertBuiltIn => settings.MobileAlertBuiltIn;
    public string? MainAlertAssetId => settings.MobileAlertAssetId;
    public Task AssignMainAlertAsync(string name, int? builtIn = null, string? assetId = null) => RunAsync(() =>
    {
        settings.MobileAlertPresetName = name;
        settings.MobileAlertBuiltIn = builtIn;
        settings.MobileAlertAssetId = assetId;
        dirty = true;
        Notify(nameof(MainAlertPresetName));
        return Task.CompletedTask;
    });
    public Task SetLocalMonitorAsync(bool enabled) => RunAsync(() =>
    {
        settings.LocalToneMonitorEnabled = enabled;
        dirty = true;
        return Task.CompletedTask;
    });
    public Task SendBuiltInAsync(LegacyAlertTone tone)
        => RunAsync(() => SendAsync(LegacyAlertToneGenerator.CreateSequence(tone), ConsoleToneTargets.Alert));

    public Task ImportAudioAsync(string fileName, string mediaType, Stream content) => RunAsync(async () =>
    {
        if (assets is null) throw new InvalidOperationException("Audio asset storage is unavailable.");
        string name = string.IsNullOrWhiteSpace(AlertToneNameText) ? fileName : AlertToneNameText.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException("Alert tone names must contain 1–80 characters.");
        AssetDescriptor asset = await assets.ImportAsync(fileName, mediaType, content, OperationToken);
        var tone = new AlertToneViewModel(new AlertToneSetting
        { Name = name, AssetId = asset.Id.ToString(), FileName = fileName }, new ManagedOnlyFiles());
        workspace.MutableAlertTones.Add(tone);
        settings.AlertTones = workspace.MutableAlertTones.Select(item => item.ToSetting()).ToList();
        dirty = true;
        // Keep the app-owned asset with a failed save so the operator can retry without reopening Files.
        await SavePendingAsync();
        AlertToneNameText = "";
        pendingStatus = $"Alert audio '{name}' imported.";
    });
    public Task SendAlertAsync(AlertToneViewModel tone) => RunAsync(async () =>
    {
        if (!Guid.TryParse(tone.AssetId, out Guid id)) throw new InvalidOperationException("Import this audio into the app before sending it.");
        await SavePendingAsync();
        if (session.Commands is not IConsoleToneCommands commands) throw new InvalidOperationException("Open a live configuration first.");
        await commands.SendAlertAudioAsync(new AssetId(id), OperationToken);
        pendingStatus = session.Snapshot.StatusText;
    });
    public Task DeleteAlertAsync(AlertToneViewModel tone) => EditAsync(() =>
    {
        int index = workspace.MutableAlertTones.IndexOf(tone);
        if (index < 0) return;
        workspace.MutableAlertTones.RemoveAt(index);
        settings.AlertTones = workspace.MutableAlertTones.Select(item => item.ToSetting()).ToList();
        dirty = true;
        ((ITonePresentationSession)this).BeginUndoableAction("Audio removed", () =>
        {
            workspace.MutableAlertTones.Insert(Math.Min(index, workspace.MutableAlertTones.Count), tone);
            settings.AlertTones = workspace.MutableAlertTones.Select(item => item.ToSetting()).ToList();
            dirty = true;
            return ValueTask.CompletedTask;
        }, () => QueueAssetCleanupAsync(tone.AssetId));
        undoAsset = Guid.TryParse(tone.AssetId, out Guid assetId) ? new AssetId(assetId) : null;
        pendingStatus = "Alert audio removed. Undo is available for 8 seconds.";
    });
    public Task SendDtmfAsync(DtmfPresetViewModel? preset = null) => RunAsync(async () =>
    {
        GeneratedToneSequence sequence;
        if (preset is null)
        {
            string digits = TonePresentationController.NormalizeDtmfInput(DtmfDigits);
            settings.LastDtmfDigits = digits;
            dirty = true;
            sequence = ToneEditorSequences.Dtmf(digits);
        }
        else sequence = ToneEditorSequences.DtmfPreset(preset);
        await SendAsync(sequence, ConsoleToneTargets.Alert);
    });
    public Task SendPatternAsync() => RunAsync(async () =>
    {
        if (!Editor.TryBuildToneSequence(out var sequence, out string? error)) throw new ArgumentException(error);
        var first = workspace.ToneSequenceSteps.First(step => !step.IsSilence);
        settings.ToneFrequencyHz = double.Parse(first.FrequencyText, CultureInfo.InvariantCulture);
        settings.ToneDurationSeconds = double.Parse(first.DurationText, CultureInfo.InvariantCulture);
        dirty = true;
        await SendAsync(sequence!, ConsoleToneTargets.Alert);
    });
    public Task SendPresetAsync(TonePresetViewModel preset)
        => RunAsync(() => SendAsync(ToneEditorSequences.TonePreset(preset), ConsoleToneTargets.Alert));
    public Task SendQuickCallAsync() => RunAsync(async () =>
    {
        if (!QuickCallToneGenerator.TryParse(QuickCallToneAText, QuickCallToneBText, out double a, out double b, out string? error))
            throw new ArgumentException(error);
        settings.QuickCallToneAFrequencyHz = a;
        settings.QuickCallToneBFrequencyHz = b;
        dirty = true;
        var targets = session.Snapshot.Channels.Values.Where(channel => channel.PageSelected).Select(channel => channel.Id).ToArray();
        await SendAsync(QuickCallToneGenerator.CreateSequence(a, b), ConsoleToneTargets.Page);
        foreach (var id in targets) await session.Commands.SetPageSelectedAsync(id, false);
    });
    private async Task SendAsync(GeneratedToneSequence sequence, ConsoleToneTargets targets)
    {
        if (session.Commands is not IConsoleToneCommands commands) throw new InvalidOperationException("Open a live configuration first.");
        await SavePendingAsync();
        await commands.SendToneAsync(sequence, targets, OperationToken);
        pendingStatus = session.Snapshot.StatusText;
    }
    public async Task CancelAsync()
    {
        try
        {
            operationCancellation?.Cancel();
            if (session.Commands is IConsoleToneCommands commands) await commands.CancelTonesAsync();
            await activeOperation;
        }
        catch (Exception exception) { Status = exception.Message; Notify(nameof(Status)); }
    }
    void ITonePresentationSession.PersistUserSettings() => dirty = true;
    void ITonePresentationSession.SetTransmitStatus(string status) => pendingStatus = status;
    void ITonePresentationSession.BeginUndoableAction(string message, Func<ValueTask> action, Func<ValueTask>? commit)
    {
        undoAsset = null;
        undo.Begin(message, action, commit);
    }
    private void UndoChanged(object? sender, EventArgs args)
    {
        if (!undo.CanUndo) undoAsset = null;
        Notify(nameof(CanUndo));
    }
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs args) => PropertyChanged?.Invoke(this, args);
    private void SnapshotChanged(object? sender, ConsoleSnapshotChangedEventArgs args)
    {
        // Audio activity and meter updates do not rebuild the editor's destination summaries.
        foreach (var (id, current) in args.Current.Channels)
        {
            if (args.Previous.Channels.TryGetValue(id, out var previous) &&
                previous.AlertSelected == current.AlertSelected && previous.PageSelected == current.PageSelected) continue;
            Dispatcher.UIThread.Post(() => { if (!disposed) RefreshTargets(); });
            return;
        }
    }
    private void RefreshTargets()
    {
        var snapshot = session.Snapshot;
        var channels = session.Topology.Channels;
        workspace.UpdateTargets(
            ToneTargetSummary.Create("ALERT", channels.Where(channel => snapshot.Channels.TryGetValue(channel.Id, out var control) && control.AlertSelected)
                .Select(channel => (channel.SystemId.Value, channel.Name))),
            ToneTargetSummary.Create("PAGE", channels.Where(channel => snapshot.Channels.TryGetValue(channel.Id, out var control) && control.PageSelected)
                .Select(channel => (channel.SystemId.Value, channel.Name))));
    }
    private void Notify(string property) => PropertyChanged?.Invoke(this, new(property));
    public void Dispose()
    {
        DvmConsole.Threading.TaskObservation.Observe(DisposeAsync().AsTask());
    }
    public ValueTask DisposeAsync()
    {
        if (retirement is not null) return new(retirement);
        disposed = true;
        undo.Changed -= UndoChanged;
        workspace.PropertyChanged -= WorkspaceChanged;
        session.SnapshotChanged -= SnapshotChanged;
        return new(retirement = RetireAsync());
    }
    private async Task RetireAsync()
    {
        await CancelAsync();
        undoAsset = null;
        await undo.DisposeAsync();
        await CleanUpAssetsAsync();
    }
    private sealed class ManagedOnlyFiles : ILegacyAlertToneFiles
    {
        public string GetFileName(string path) => path;
        public bool Exists(string path) => false;
    }
    private sealed class Command(Func<Task> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => DvmConsole.Threading.TaskObservation.Observe(execute());
    }
}
