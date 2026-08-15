using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DvmConsole.Desktop;

public sealed class PatchGroupEditorViewModel : INotifyPropertyChanged
{
    private bool enabled;
    private bool oneWay;
    private bool pttActive;
    private string conflictSummary = string.Empty;

    public PatchGroupEditorViewModel(
        string name,
        bool enabled,
        bool oneWay,
        IEnumerable<PatchMemberEditorViewModel> members,
        bool isMultiSelect = false)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A patch group name is required.", nameof(name))
            : name.Trim();
        this.enabled = enabled;
        this.oneWay = oneWay;
        IsMultiSelect = isMultiSelect;
        Members = new ObservableCollection<PatchMemberEditorViewModel>(members ?? []);
        foreach (PatchMemberEditorViewModel member in Members)
            member.PropertyChanged += HandleMemberPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? MembershipChanged;
    public string Name { get; }
    public bool IsMultiSelect { get; }
    public bool IsPatchGroup => !IsMultiSelect;
    public string GroupTypeText => IsMultiSelect ? "Multi-select" : "Patch";
    public ObservableCollection<PatchMemberEditorViewModel> Members { get; }
    public bool IsPttActive => pttActive;
    public string PttButtonText => pttActive ? "Stop Multi-select PTT" : "Multi-select PTT";
    public bool HasConflicts => conflictSummary.Length > 0;
    public string ConflictSummary => conflictSummary;

    public bool IsEnabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
                return;
            enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public bool IsOneWay
    {
        get => oneWay;
        set
        {
            if (oneWay == value)
                return;
            oneWay = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOneWay)));
        }
    }

    public void SetPttActive(bool value)
    {
        if (pttActive == value)
            return;
        pttActive = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPttActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttButtonText)));
    }

    public void SetConflictSummary(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (conflictSummary == normalized)
            return;
        conflictSummary = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasConflicts)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConflictSummary)));
    }

    private void HandleMemberPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PatchMemberEditorViewModel.IsMember))
            MembershipChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class PatchMemberEditorViewModel : INotifyPropertyChanged
{
    private bool member;
    private string conflictText = string.Empty;

    public PatchMemberEditorViewModel(ChannelViewModel channel, bool member)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.member = member;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ChannelViewModel Channel { get; }
    public string DisplayName =>
        $"{Channel.Definition.SystemName} / {Channel.Name} ({Channel.ModeText} TGID {Channel.Definition.DestinationId})";
    public bool CanTransmit => Channel.CanTransmit;
    public bool HasConflict => conflictText.Length > 0;
    public string ConflictText => conflictText;

    public bool IsMember
    {
        get => member;
        set
        {
            if (member == value)
                return;
            member = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMember)));
        }
    }

    public void SetConflictText(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (conflictText == normalized)
            return;
        conflictText = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasConflict)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConflictText)));
    }
}
