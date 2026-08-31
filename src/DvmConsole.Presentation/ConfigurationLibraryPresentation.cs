using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DvmConsole.Application;

namespace DvmConsole.Presentation;

public sealed class ConfigurationLibraryItemViewModel
{
    public ConfigurationLibraryItemViewModel(ConfigurationSummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    public ConfigurationSummary Summary { get; }
    public ConfigurationReference Reference => new(Summary.Id, Summary.CurrentRevision);
    public string Name => Summary.Name;
    public string RevisionText => Summary.IsLegacyCandidate
        ? "Not imported yet"
        : $"Revision {Summary.CurrentRevision.Value:N}"[..17];
    public string ModifiedText => Summary.ModifiedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string StateText => Summary.IsActive
        ? Summary.PendingReload ? "Running an earlier revision · reload pending" : "Running"
        : Summary.IsLegacyCandidate ? "Legacy YAML candidate"
        : Summary.IsReadOnly ? "Read-only YAML" : "Ready";
    public string ActivateText => Summary.IsLegacyCandidate
        ? "Import YAML"
        : Summary.PendingReload ? "Reload latest" : Summary.IsActive ? "Running" : "Use configuration";
    public bool CanActivate => Summary.IsLegacyCandidate
        ? !string.IsNullOrWhiteSpace(Summary.LegacyOriginIdentity)
        : !Summary.IsActive || Summary.PendingReload;
    public bool CanMoveToTrash => !Summary.IsActive;
    public bool IsLegacyCandidate => Summary.IsLegacyCandidate;
    public string? LegacyOriginIdentity => Summary.LegacyOriginIdentity;
}

public sealed class ConfigurationLibraryViewModel : INotifyPropertyChanged
{
    private string statusText = "Loading configurations…";
    private bool isBusy;

    public ObservableCollection<ConfigurationLibraryItemViewModel> Configurations { get; } = [];
    public ObservableCollection<ConfigurationLibraryItemViewModel> Trash { get; } = [];

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        set => SetField(ref isBusy, value);
    }

    public bool HasConfigurations => Configurations.Count > 0;
    public bool HasTrash => Trash.Count > 0;
    public string TrashSummary => Trash.Count == 0
        ? "Trash"
        : $"Trash ({Trash.Count.ToString(CultureInfo.CurrentCulture)})";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Replace(
        IEnumerable<ConfigurationSummary> configurations,
        IEnumerable<ConfigurationSummary> trash)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        ArgumentNullException.ThrowIfNull(trash);
        Configurations.Clear();
        foreach (ConfigurationSummary summary in configurations)
            Configurations.Add(new ConfigurationLibraryItemViewModel(summary));
        Trash.Clear();
        foreach (ConfigurationSummary summary in trash)
            Trash.Add(new ConfigurationLibraryItemViewModel(summary));
        int managedCount = Configurations.Count(item => !item.IsLegacyCandidate);
        int candidateCount = Configurations.Count - managedCount;
        StatusText = managedCount == 0 && candidateCount == 0
            ? "No saved configurations. Create one in Configuration Studio or import YAML."
            : DescribeCatalog(managedCount, candidateCount);
        OnPropertyChanged(nameof(HasConfigurations));
        OnPropertyChanged(nameof(HasTrash));
        OnPropertyChanged(nameof(TrashSummary));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string DescribeCatalog(int managedCount, int candidateCount)
    {
        var parts = new List<string>(2);
        if (managedCount > 0)
        {
            parts.Add($"{managedCount.ToString(CultureInfo.CurrentCulture)} saved " +
                $"configuration{(managedCount == 1 ? string.Empty : "s")}");
        }
        if (candidateCount > 0)
        {
            parts.Add($"{candidateCount.ToString(CultureInfo.CurrentCulture)} legacy " +
                $"candidate{(candidateCount == 1 ? string.Empty : "s")}");
        }
        return string.Join(" · ", parts);
    }
}

public sealed class ConfigurationLibraryItemEventArgs(ConfigurationLibraryItemViewModel item) : EventArgs
{
    public ConfigurationLibraryItemViewModel Item { get; } = item ?? throw new ArgumentNullException(nameof(item));
}
