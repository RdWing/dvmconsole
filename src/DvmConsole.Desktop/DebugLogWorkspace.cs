using DvmConsole.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;

namespace DvmConsole.Desktop;

// Owns diagnostic ingestion, retention, filtering, and export for one console
// session. MainWindowViewModel only forwards the binding-facing surface.
internal sealed class DebugLogWorkspace : INotifyPropertyChanged, IDisposable
{
    private static readonly IReadOnlyList<string> SeverityFilters =
        Array.AsReadOnly(["All", "Debug", "Info", "Warning", "Error", "Fatal"]);

    private readonly BoundedDebugLogBuffer buffer = new();
    private readonly FilteredDebugLogCollection filtered;
    private readonly DebugLogDrainController drain;
    private string filterText = string.Empty;
    private string severityFilter = "Info";

    public DebugLogWorkspace(
        Func<bool> hasUiThreadAccess,
        Action<Action> postToUiThread,
        Func<bool> isStopped)
    {
        ArgumentNullException.ThrowIfNull(hasUiThreadAccess);
        ArgumentNullException.ThrowIfNull(postToUiThread);
        ArgumentNullException.ThrowIfNull(isStopped);
        filtered = new FilteredDebugLogCollection(buffer.Entries);
        drain = new DebugLogDrainController(
            hasUiThreadAccess,
            postToUiThread,
            PublishBatch,
            isStopped);
        Entries = new ReadOnlyObservableCollection<DebugLogEntry>(buffer.Entries);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event NotifyCollectionChangedEventHandler? CollectionChanging
    {
        add => filtered.CollectionChanging += value;
        remove => filtered.CollectionChanging -= value;
    }

    public IReadOnlyList<string> DebugLogSeverityFilters => SeverityFilters;
    public ReadOnlyObservableCollection<DebugLogEntry> Entries { get; }
    public IReadOnlyList<DebugLogEntry> FilteredEntries => filtered.Entries;
    public string RetentionText => buffer.RetentionText;

    public string FilterText
    {
        get => filterText;
        set
        {
            string normalized = value ?? string.Empty;
            if (filterText == normalized)
                return;

            filterText = normalized;
            filtered.SetFilter(SeverityFilter, normalized);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilterText)));
        }
    }

    public string SeverityFilter
    {
        get => severityFilter;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "All" : value;
            if (severityFilter == normalized)
                return;

            severityFilter = normalized;
            filtered.SetFilter(normalized, FilterText);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeverityFilter)));
        }
    }

    public void Add(
        DateTimeOffset timestamp,
        string source,
        DebugLogSeverity severity,
        string message)
    {
        DebugLogEntry entry = BoundedDebugLogBuffer.PrepareForRetention(new DebugLogEntry(
            timestamp,
            source,
            severity,
            DebugLogRedactor.Redact(message)));
        drain.Enqueue(entry);
    }

    public int Export(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var writer = new StreamWriter(fullPath, append: false);
        writer.WriteLine("Timestamp\tSeverity\tSource\tMessage");
        foreach (DebugLogEntry entry in Entries.Reverse())
        {
            writer.Write(entry.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.Write('\t');
            writer.Write(entry.SeverityText);
            writer.Write('\t');
            writer.Write(entry.Source);
            writer.Write('\t');
            writer.WriteLine(DebugLogRedactor.Redact(entry.Message).Replace("\r", " ").Replace("\n", " "));
        }

        return Entries.Count;
    }

    public void Dispose()
        => filtered.Dispose();

    private void PublishBatch(IReadOnlyList<DebugLogEntry> batch)
    {
        buffer.AddRange(batch);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RetentionText)));
    }
}
