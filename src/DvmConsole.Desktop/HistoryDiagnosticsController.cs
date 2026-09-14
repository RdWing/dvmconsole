// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;
using DvmConsole.Application;

namespace DvmConsole.Desktop;

internal interface IHistoryDiagnosticsSession
{
    SystemViewModel? SelectedSystem { get; }
    bool CheckUiAccess();
    void PostToUi(Action action);
    void PublishStatus(string text);
    void NotifyPropertyChanged(string propertyName);
}

internal sealed class HistoryDiagnosticsSessionPort(
    Func<SystemViewModel?> selectedSystem,
    Func<bool> checkUiAccess,
    Action<Action> postToUi,
    Action<string> publishStatus,
    Action<string> notifyPropertyChanged) : IHistoryDiagnosticsSession
{
    public SystemViewModel? SelectedSystem => selectedSystem();
    public bool CheckUiAccess() => checkUiAccess();
    public void PostToUi(Action action) => postToUi(action);
    public void PublishStatus(string text) => publishStatus(text);
    public void NotifyPropertyChanged(string propertyName) => notifyPropertyChanged(propertyName);
}

/// <summary>
/// Owns History commands, Activity projection, redacted diagnostic export,
/// and subscriber-command audit retention behind the main-window facade.
/// </summary>
internal sealed class HistoryDiagnosticsController : IDisposable
{
    private readonly ConsoleSubscriberCommandDispatcher subscriberCommands;
    private readonly HistoryRecordingController history;
    private readonly DebugLogWorkspace debugLogs;
    private readonly IHistoryDiagnosticsSession session;
    private readonly ObservableCollection<SubscriberCommandAuditEntry> subscriberCommandAudit = [];

    public HistoryDiagnosticsController(
        HistoryRecordingController history,
        DebugLogWorkspace debugLogs,
        IHistoryDiagnosticsSession session,
        ConsoleSubscriberCommandDispatcher? subscriberCommands = null)
    {
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.debugLogs = debugLogs ?? throw new ArgumentNullException(nameof(debugLogs));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.subscriberCommands = subscriberCommands ?? new(SystemClock.Instance, historyLimit: 50);
        this.subscriberCommands.HistoryChanged += HandleSubscriberHistoryChanged;
        SubscriberCommandAudit = new ReadOnlyObservableCollection<SubscriberCommandAuditEntry>(
            subscriberCommandAudit);
    }

    private void HandleSubscriberHistoryChanged(object? sender, EventArgs args)
    {
        if (session.CheckUiAccess()) ProjectSubscriberAudit();
        else session.PostToUi(ProjectSubscriberAudit);
    }

    public void Dispose() => subscriberCommands.HistoryChanged -= HandleSubscriberHistoryChanged;

    public ReadOnlyObservableCollection<SubscriberCommandAuditEntry> SubscriberCommandAudit { get; }

    public IReadOnlyList<SubscriberCommandAuditEntry> ActivitySubscriberCommandAudit
        => session.SelectedSystem is not { } system
            ? []
            : SubscriberCommandAudit
                .Where(entry => entry.SystemName.Equals(system.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();

    public void ClearHistory()
    {
        history.History.Clear();
        RefreshHistory();
        session.PublishStatus("Activity history cleared.");
    }

    public void AddEvent(
        string source,
        string message,
        string? ridText = null,
        string? tgidText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        void Apply()
        {
            history.History.AddEvent(DateTimeOffset.Now, source, message, ridText, tgidText);
            RefreshHistory();
        }

        if (session.CheckUiAccess())
            Apply();
        else
            session.PostToUi(Apply);
    }

    public void ExportHistory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using FileStream destination = new(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        ExportHistory(destination);
    }

    public void ExportHistory(Stream destination, bool leaveOpen = false)
    {
        CallHistoryCsv.Write(destination, history.CallHistory.Select(entry => new CallHistoryExportRow(
            entry.Timestamp, entry.EndTimestamp, entry.Duration, entry.SystemName,
            entry.DisplayChannelText, entry.DisplaySourceText, entry.CallerText,
            entry.DisplayDestinationText, entry.ProtocolText, entry.EncryptionText, entry.StreamId)), leaveOpen);
        session.PublishStatus(
            $"Exported {history.CallHistory.Count} activity-history " +
            $"entr{(history.CallHistory.Count == 1 ? "y" : "ies")}.");
    }

    public void ExportDebugLogs(string path)
    {
        int count = debugLogs.Export(path);
        ReportDebugExportSuccess(count, path);
    }

    public void ExportDebugLogs(Stream destination, string destinationName)
    {
        int count = debugLogs.Export(destination);
        ReportDebugExportSuccess(count, destinationName);
    }

    public void ReportDebugExportFailure(string message)
        => session.PublishStatus($"Unable to export debug logs: {message}");

    public void AddDebugLog(
        DateTimeOffset timestamp,
        string source,
        DebugLogSeverity severity,
        string message)
        => debugLogs.Add(timestamp, source, severity, message);

    public bool TrySendSubscriberCommand(
        SystemViewModel system,
        P25SubscriberCommand command,
        string? destinationText,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(system);

        // Text parsing and observable projection belong to the desktop adapter.
        // Validation, transport submission and authoritative audit live in Application.
        if (!P25SubscriberCommandCodec.TryParseSubscriberId(destinationText, out uint destinationId))
            destinationId = 0;
        var result = subscriberCommands.Submit(system.Id, system, system,
            FneSubscriberCommandBindings.ToApplication(command), destinationId);
        message = result.Detail;
        session.PublishStatus(result.StatusText);
        return result.Submitted;
    }

    public void ToggleActivityZoneFilter()
    {
        history.ToggleActivityZoneFilter();
        RefreshActivityHistory();
    }

    public void ToggleActivityReceiveFilter()
    {
        history.ToggleActivityReceiveFilter();
        RefreshActivityHistory();
    }

    public void RefreshHistory()
    {
        history.RefreshFilteredCallHistory();
        RefreshActivityHistory();
    }

    public void RefreshActivityHistory()
    {
        SystemViewModel? system = session.SelectedSystem;
        history.RefreshActivityCallHistory(
            system?.Name,
            system?.SelectedZone?.Channels.Select(channel => channel.Name),
            system?.Channels
                .Where(channel => channel.IsAudioEnabled)
                .Select(channel => channel.Name));
    }

    public void NotifySelectedSystemChanged()
    {
        RefreshActivityHistory();
        session.NotifyPropertyChanged(nameof(MainWindowViewModel.ActivitySubscriberCommandAudit));
    }

    public void AcknowledgeSubscriber(ConsoleSubscriberAcknowledgement response)
    {
        if (subscriberCommands.Acknowledge(response) is not { } result) return;
        if (session.CheckUiAccess()) session.PublishStatus(result.StatusText);
        else session.PostToUi(() => session.PublishStatus(result.StatusText));
    }

    public void InterruptSubscriberCommands(SystemId system) => subscriberCommands.Interrupt(system);
    public void ExpireSubscriberCommands() => subscriberCommands.Expire();

    private void ProjectSubscriberAudit()
    {
        subscriberCommandAudit.Clear();
        foreach (var result in subscriberCommands.History)
            subscriberCommandAudit.Add(new SubscriberCommandAuditEntry(
                result.Timestamp, result.SystemName, FneSubscriberCommandBindings.ToFne(result.Command),
                result.DestinationId, result.Submitted, result.Detail));
        session.NotifyPropertyChanged(nameof(MainWindowViewModel.ActivitySubscriberCommandAudit));
    }

    private void ReportDebugExportSuccess(int count, string destinationName)
        => session.PublishStatus($"Exported {count} redacted debug log " +
            $"entr{(count == 1 ? "y" : "ies")} to {destinationName}.");

}
