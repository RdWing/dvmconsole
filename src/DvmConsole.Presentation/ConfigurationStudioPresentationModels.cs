using DvmConsole.Core.Configuration;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace DvmConsole.Presentation;

public sealed record ConfigurationStreamRow(ZoneConfiguration Zone, WebStreamConfiguration Stream)
{
    public string ZoneName => Zone.Name;
}

public sealed class ConfigurationAliasRow : INotifyPropertyChanged
{
    public ConfigurationAliasRow(string identifier, RadioAlias alias)
    {
        Identifier = identifier;
        Alias = alias;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Identifier { get; }
    public RadioAlias Alias { get; }
    public uint Rid
    {
        get => Alias.Rid;
        set
        {
            if (Alias.Rid == value)
                return;
            Alias.Rid = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rid)));
        }
    }
    public string Name
    {
        get => Alias.Alias;
        set
        {
            if (string.Equals(Alias.Alias, value, StringComparison.Ordinal))
                return;
            Alias.Alias = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }
}

public sealed record ConfigurationCardSizeOption(string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class ConfigurationChannelRow : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<int> DmrSlots = [1, 2];
    private static readonly IReadOnlyList<ConfigurationCardSizeOption> CardSizes =
    [
        new("small", "Small"),
        new("normal", "Normal"),
        new("large", "Large")
    ];
    private string lastMode;

    public ConfigurationChannelRow(int number, ChannelConfiguration channel, bool canEdit)
    {
        Number = number;
        Channel = channel;
        CanEdit = canEdit;
        lastMode = channel.Mode;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public int Number { get; private set; }
    public ChannelConfiguration Channel { get; }
    public bool CanEdit { get; }
    public bool CanEditSlot => CanEdit && IsDmr;
    public IReadOnlyList<int> SlotOptions => DmrSlots;
    public IReadOnlyList<ConfigurationProtocolOption> ModeOptions => ConfigurationProtocolCatalog.ForChannels;
    public IReadOnlyList<EncryptionAlgorithmOption> AvailableAlgorithms =>
        EncryptionAlgorithmCatalog.ForChannelMode(Channel.Mode);
    public string Name
    {
        get => Channel.Name;
        set
        {
            if (string.Equals(Channel.Name, value, StringComparison.Ordinal))
                return;
            Channel.Name = value ?? string.Empty;
            Notify(nameof(Name));
        }
    }
    public string DestinationId
    {
        get => Channel.Tgid;
        set
        {
            if (string.Equals(Channel.Tgid, value, StringComparison.Ordinal))
                return;
            Channel.Tgid = value ?? string.Empty;
            Notify(nameof(DestinationId), nameof(DestinationText));
        }
    }
    public string Mode
    {
        get => Channel.Mode;
        set
        {
            if (string.Equals(Channel.Mode, value, StringComparison.OrdinalIgnoreCase))
                return;
            Channel.Mode = value ?? string.Empty;
            Notify(
                nameof(Mode),
                nameof(ModeText),
                nameof(DestinationText),
                nameof(IsDmr),
                nameof(CanEditSlot),
                nameof(SlotText),
                nameof(AvailableAlgorithms),
                nameof(SelectedAlgorithm),
                nameof(EncryptionText));
        }
    }
    public bool IsDmr => string.Equals(Channel.Mode, "dmr", StringComparison.OrdinalIgnoreCase);
    public int Slot
    {
        get => Channel.Slot;
        set
        {
            if (Channel.Slot == value)
                return;
            Channel.Slot = value;
            Notify(nameof(Slot), nameof(SlotText));
        }
    }
    public EncryptionAlgorithmOption? SelectedAlgorithm
    {
        get => EncryptionAlgorithmCatalog.FindChannelOption(Channel.Mode, Channel.Algo);
        set
        {
            if (value is null ||
                string.Equals(Channel.Algo, value.ConfigurationValue, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            Channel.Algo = value.ConfigurationValue;
            Notify(nameof(SelectedAlgorithm), nameof(EncryptionText));
        }
    }
    public string System => Channel.System;
    public string DestinationText => $"TG {Channel.Tgid} - {ConfigurationProtocolCatalog.DisplayName(Channel.Mode)}";
    public string ModeText => ConfigurationProtocolCatalog.DisplayName(Channel.Mode);
    public string SlotText => IsDmr
        ? Channel.Slot.ToString(CultureInfo.InvariantCulture)
        : string.Empty;
    public string EncryptionText => string.IsNullOrWhiteSpace(Channel.Algo) ||
                                    string.Equals(Channel.Algo, "none", StringComparison.OrdinalIgnoreCase)
        ? "None"
        : string.IsNullOrWhiteSpace(Channel.KeyId) || Channel.KeyId == "0"
            ? Channel.Algo.ToUpperInvariant()
            : $"Key {Channel.KeyId}";
    public bool RxOnly
    {
        get => Channel.RxOnly;
        set
        {
            if (Channel.RxOnly == value)
                return;
            Channel.RxOnly = value;
            Notify(nameof(RxOnly));
        }
    }
    public string CardSize
    {
        get => Channel.CardSize;
        set
        {
            if (string.Equals(Channel.CardSize, value, StringComparison.OrdinalIgnoreCase))
                return;
            Channel.CardSize = value ?? "normal";
            Notify(nameof(CardSize), nameof(CardSizeText));
        }
    }
    public IReadOnlyList<ConfigurationCardSizeOption> CardSizeOptions => CardSizes;
    public ConfigurationCardSizeOption? SelectedCardSize
    {
        get => CardSizes.FirstOrDefault(option => option.Value.Equals(
            Channel.CardSize,
            StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                CardSize = value.Value;
        }
    }
    public string CardSizeText => string.Equals(Channel.CardSize, "normal", StringComparison.OrdinalIgnoreCase)
        ? "Normal"
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Channel.CardSize ?? "normal");

    public void Refresh(int number)
    {
        bool modeChanged = !string.Equals(lastMode, Channel.Mode, StringComparison.OrdinalIgnoreCase);
        lastMode = Channel.Mode;
        Number = number;
        Notify(
            nameof(Number), nameof(Name), nameof(DestinationId), nameof(System), nameof(DestinationText),
            nameof(Mode), nameof(ModeText), nameof(IsDmr), nameof(CanEditSlot), nameof(Slot), nameof(SlotText),
            nameof(SelectedAlgorithm), nameof(EncryptionText),
            nameof(RxOnly), nameof(CardSize), nameof(SelectedCardSize), nameof(CardSizeText));
        if (modeChanged)
            Notify(nameof(AvailableAlgorithms));
    }

    private void Notify(params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ConfigurationHierarchyNode :
    INotifyPropertyChanged,
    IConfigurationHierarchyNode
{
    private bool isExpanded;

    public ConfigurationHierarchyNode(
        string fallbackLabel,
        SystemConfiguration? system = null,
        ZoneConfiguration? zone = null,
        ChannelConfiguration? channel = null,
        bool isExpanded = false)
    {
        FallbackLabel = fallbackLabel;
        System = system;
        Zone = zone;
        Channel = channel;
        this.isExpanded = isExpanded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ConfigurationHierarchyNode> Children { get; } = [];
    System.Collections.IEnumerable IConfigurationHierarchyNode.Children => Children;
    public SystemConfiguration? System { get; }
    public ZoneConfiguration? Zone { get; }
    public ChannelConfiguration? Channel { get; }
    public string FallbackLabel { get; }
    public bool IsSystem => Zone is null && Channel is null;
    public bool IsZone => Zone is not null && Channel is null;
    public bool IsChannel => Channel is not null;
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value)
                return;
            isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
    public string Label => Channel?.Name ?? Zone?.Name ?? System?.Name ?? FallbackLabel;
    public string CountText => IsSystem
        ? $"{Children.Count} zone{PluralSuffix}"
        : IsZone
            ? $"{Children.Count} channel{PluralSuffix}"
            : string.Empty;
    private string PluralSuffix => Children.Count == 1 ? string.Empty : "s";

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
    }
}
