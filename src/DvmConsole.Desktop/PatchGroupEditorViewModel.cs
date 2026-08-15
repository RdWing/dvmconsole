using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DvmConsole.Desktop;

public sealed class PatchGroupEditorViewModel : INotifyPropertyChanged
{
    private bool enabled;
    private bool oneWay;

    public PatchGroupEditorViewModel(
        string name,
        bool enabled,
        bool oneWay,
        IEnumerable<PatchMemberEditorViewModel> members)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A patch group name is required.", nameof(name))
            : name.Trim();
        this.enabled = enabled;
        this.oneWay = oneWay;
        Members = new ObservableCollection<PatchMemberEditorViewModel>(members ?? []);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; }
    public ObservableCollection<PatchMemberEditorViewModel> Members { get; }

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
}

public sealed class PatchMemberEditorViewModel : INotifyPropertyChanged
{
    private bool member;

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
}
