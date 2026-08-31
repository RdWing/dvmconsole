using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DvmConsole.Presentation;

public sealed class PatchGroupEditorViewModel : INotifyPropertyChanged
{
    private bool enabled;
    private bool oneWay;
    private bool pttActive;
    private PatchMemberEditorViewModel? selectedSource;
    private string conflictSummary = string.Empty;

    public PatchGroupEditorViewModel(
        string name,
        bool enabled,
        bool oneWay,
        IEnumerable<PatchMemberEditorViewModel> members,
        bool isMultiSelect = false,
        string? oneWaySourceKey = null)
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
        RefreshMemberSelectionEligibility();
        RefreshSourceOptions(oneWaySourceKey);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? MembershipChanged;
    public string Name { get; }
    public bool IsMultiSelect { get; }
    public bool IsPatchGroup => !IsMultiSelect;
    public string GroupTypeText => IsMultiSelect ? "Multi-select" : "Patch";
    public ObservableCollection<PatchMemberEditorViewModel> Members { get; }
    public ObservableCollection<PatchMemberEditorViewModel> SourceOptions { get; } = [];
    public bool IsPttActive => pttActive;
    public string PttButtonText => pttActive ? "Stop Multi-select PTT" : "Multi-select PTT";
    public bool HasConflicts => conflictSummary.Length > 0;
    public string ConflictSummary => conflictSummary;
    public string MemberEditorHeader => $"Edit members ({Members.Count(member => member.IsMember)} selected)";
    public string OneWayDestinationSummary
    {
        get
        {
            int destinationCount = Members.Count(member => member.IsMember && !ReferenceEquals(member, selectedSource));
            return selectedSource is null
                ? "Select at least one member, then choose the source."
                : destinationCount == 0
                    ? "Add at least one destination member."
                    : $"{destinationCount} destination{(destinationCount == 1 ? string.Empty : "s")}: " +
                      string.Join(", ", Members
                          .Where(member => member.IsMember && !ReferenceEquals(member, selectedSource))
                          .Select(member => member.Channel.Name));
        }
    }

    public PatchMemberEditorViewModel? SelectedSource
    {
        get => selectedSource;
        set
        {
            PatchMemberEditorViewModel? normalized = value is not null && value.IsMember && Members.Contains(value)
                ? value
                : null;
            if (ReferenceEquals(selectedSource, normalized))
                return;
            selectedSource = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OneWayDestinationSummary)));
        }
    }

    public IReadOnlyList<PatchMemberEditorViewModel> GetMembersInRoutingOrder()
    {
        List<PatchMemberEditorViewModel> selected = Members.Where(member => member.IsMember).ToList();
        if (!IsOneWay || selectedSource is null)
            return selected;

        selected.Remove(selectedSource);
        selected.Insert(0, selectedSource);
        return selected;
    }

    public string? GetMembershipValidationError()
    {
        PatchMemberEditorViewModel[] selectedMembers = Members
            .Where(member => member.IsMember)
            .ToArray();
        if (selectedMembers.Length == 0)
            return null;

        if (!IsOneWay)
        {
            PatchMemberEditorViewModel[] invalidMembers = selectedMembers
                .Where(member => !member.CanTransmit)
                .ToArray();
            return invalidMembers.Length == 0
                ? null
                : $"These members cannot transmit: {FormatMemberNames(invalidMembers)}.";
        }

        if (SelectedSource is null || !SelectedSource.CanReceive)
            return "Choose a receive-capable source for the one-way patch.";

        PatchMemberEditorViewModel[] invalidDestinations = selectedMembers
            .Where(member => !ReferenceEquals(member, SelectedSource) && !member.CanTransmit)
            .ToArray();
        return invalidDestinations.Length == 0
            ? null
            : $"One-way patch destinations must be transmit-capable: {FormatMemberNames(invalidDestinations)}.";
    }

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
            RefreshMemberSelectionEligibility();
            RefreshSourceOptions(selectedSource?.RoutingKey);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOneWay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OneWayDestinationSummary)));
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
        if (args.PropertyName == nameof(PatchMemberEditorViewModel.CanReceive))
        {
            RefreshSourceOptions(selectedSource?.RoutingKey);
            return;
        }
        if (args.PropertyName != nameof(PatchMemberEditorViewModel.IsMember))
            return;

        RefreshSourceOptions(selectedSource?.RoutingKey);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MemberEditorHeader)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OneWayDestinationSummary)));
        MembershipChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshSourceOptions(string? preferredSourceKey)
    {
        PatchMemberEditorViewModel[] selectedMembers = Members
            .Where(member => member.IsMember && member.CanReceive)
            .ToArray();
        SourceOptions.Clear();
        foreach (PatchMemberEditorViewModel member in selectedMembers)
            SourceOptions.Add(member);

        SelectedSource = selectedMembers.FirstOrDefault(member =>
                string.Equals(member.RoutingKey, preferredSourceKey, StringComparison.OrdinalIgnoreCase)) ??
            selectedMembers.FirstOrDefault();
    }

    private void RefreshMemberSelectionEligibility()
    {
        bool allowReceiveOnlySource = IsPatchGroup && IsOneWay;
        foreach (PatchMemberEditorViewModel member in Members)
            member.SetReceiveOnlySourceAllowed(allowReceiveOnlySource);
    }

    private static string FormatMemberNames(IEnumerable<PatchMemberEditorViewModel> members)
        => string.Join(", ", members.Select(member => member.Channel.Name));
}

public sealed class PatchMemberEditorViewModel : INotifyPropertyChanged
{
    private bool member;
    private bool receiveOnlySourceAllowed;
    private string conflictText = string.Empty;

    public PatchMemberEditorViewModel(IPatchMemberChannelViewModel channel, bool member)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.member = member;
        Channel.PropertyChanged += HandleChannelPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IPatchMemberChannelViewModel Channel { get; }
    public string RoutingKey => Channel.RoutingKey;
    public string DisplayName =>
        $"{Channel.SystemName} / {Channel.Name} ({Channel.ModeText} TGID {Channel.DestinationId})";
    public bool CanReceive => Channel.CanListen;
    public bool CanTransmit => Channel.CanTransmit;
    public bool IsSelectionEnabled => receiveOnlySourceAllowed ? CanReceive : CanTransmit;
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

    internal void SetReceiveOnlySourceAllowed(bool value)
    {
        if (receiveOnlySourceAllowed == value)
            return;
        receiveOnlySourceAllowed = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectionEnabled)));
    }

    private void HandleChannelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(IPatchMemberChannelViewModel.CanListen))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanReceive)));
            if (receiveOnlySourceAllowed)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectionEnabled)));
        }
        if (args.PropertyName == nameof(IPatchMemberChannelViewModel.CanTransmit))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanTransmit)));
            if (!receiveOnlySourceAllowed)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectionEnabled)));
        }
    }
}
