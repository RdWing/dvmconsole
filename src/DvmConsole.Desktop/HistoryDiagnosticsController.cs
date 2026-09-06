// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;

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
internal sealed class HistoryDiagnosticsController
{
    private const int MaximumSubscriberCommandAuditEntries = 50;
    private readonly HistoryRecordingController history;
    private readonly DebugLogWorkspace debugLogs;
    private readonly IHistoryDiagnosticsSession session;
    private readonly ObservableCollection<SubscriberCommandAuditEntry> subscriberCommandAudit = [];

    public HistoryDiagnosticsController(
        HistoryRecordingController history,
        DebugLogWorkspace debugLogs,
        IHistoryDiagnosticsSession session)
    {
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.debugLogs = debugLogs ?? throw new ArgumentNullException(nameof(debugLogs));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        SubscriberCommandAudit = new ReadOnlyObservableCollection<SubscriberCommandAuditEntry>(
            subscriberCommandAudit);
    }

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
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen);
        writer.WriteLine("Start,End,DurationSeconds,System,Channel,SourceId,Caller,Talkgroup,Protocol,Encryption,StreamId");
        foreach (CallHistoryEntry entry in history.CallHistory)
        {
            writer.WriteLine(string.Join(",",
                Csv(entry.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                Csv(entry.EndTimestamp?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(entry.Duration?.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(entry.SystemName),
                Csv(entry.DisplayChannelText),
                Csv(entry.DisplaySourceText),
                Csv(entry.CallerText),
                Csv(entry.DisplayDestinationText),
                Csv(entry.ProtocolText),
                Csv(entry.EncryptionText),
                entry.StreamId.ToString(CultureInfo.InvariantCulture)));
        }
        writer.Flush();
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

        if (!P25SubscriberCommandCodec.TryParseSubscriberId(destinationText, out uint destinationId))
        {
            message = "Enter a P25 subscriber RID from 1 to 16777215.";
            RecordSubscriberCommandAudit(system.Name, command, 0, false, message);
            session.PublishStatus(message);
            return false;
        }

        if (!system.IsConnected)
        {
            message = $"{system.Name} is not connected to an FNE.";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, false, message);
            session.PublishStatus(message);
            return false;
        }

        if (system.SourceId is not uint sourceId || !P25SubscriberCommandCodec.IsValidSubscriberId(sourceId))
        {
            message = $"{system.Name} does not have a configured source RID.";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, false, message);
            session.PublishStatus(message);
            return false;
        }

        try
        {
            system.SendP25SubscriberCommand(command, destinationId);
            message = "Sent; acknowledgement decoding is pending.";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, true, message);
            session.PublishStatus($"{system.Name}: {CommandName(command)} to RID {destinationId} sent.");
            return true;
        }
        catch (Exception exception)
        {
            message = $"Unable to send command: {exception.Message}";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, false, message);
            session.PublishStatus($"{system.Name}: {message}");
            return false;
        }
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

    private void RecordSubscriberCommandAudit(
        string systemName,
        P25SubscriberCommand command,
        uint destinationId,
        bool succeeded,
        string detail)
    {
        if (subscriberCommandAudit.Count >= MaximumSubscriberCommandAuditEntries)
            subscriberCommandAudit.RemoveAt(subscriberCommandAudit.Count - 1);

        subscriberCommandAudit.Insert(0, new SubscriberCommandAuditEntry(
            DateTimeOffset.UtcNow,
            systemName,
            command,
            destinationId,
            succeeded,
            detail));
        session.NotifyPropertyChanged(nameof(MainWindowViewModel.ActivitySubscriberCommandAudit));
    }

    private void ReportDebugExportSuccess(int count, string destinationName)
        => session.PublishStatus($"Exported {count} redacted debug log " +
            $"entr{(count == 1 ? "y" : "ies")} to {destinationName}.");

    private static string Csv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string CommandName(P25SubscriberCommand command)
        => command switch
        {
            P25SubscriberCommand.CallAlert => "Page",
            P25SubscriberCommand.RadioCheck => "Radio check",
            P25SubscriberCommand.Inhibit => "Inhibit",
            P25SubscriberCommand.Uninhibit => "Uninhibit",
            _ => command.ToString()
        };
}
