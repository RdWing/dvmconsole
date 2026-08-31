using DvmConsole.Core.Configuration;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace DvmConsole.Presentation;

public sealed record ConfigurationStreamRow(ZoneConfiguration Zone, WebStreamConfiguration Stream)
{
    public string ZoneName => Zone.Name;
}

public sealed record ConfigurationAliasRow(string Identifier, RadioAlias Alias);

public sealed class ConfigurationChannelRow : INotifyPropertyChanged
{
    public ConfigurationChannelRow(int number, ChannelConfiguration channel)
    {
        Number = number;
        Channel = channel;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public int Number { get; private set; }
    public ChannelConfiguration Channel { get; }
    public string Name => Channel.Name;
    public string System => Channel.System;
    public string DestinationText => $"TG {Channel.Tgid} - {ConfigurationProtocolCatalog.DisplayName(Channel.Mode)}";
    public string ModeText => ConfigurationProtocolCatalog.DisplayName(Channel.Mode);
    public string SlotText => string.Equals(Channel.Mode, "dmr", StringComparison.OrdinalIgnoreCase)
        ? Channel.Slot.ToString(CultureInfo.InvariantCulture)
        : string.Empty;
    public string EncryptionText => string.IsNullOrWhiteSpace(Channel.Algo) ||
                                    string.Equals(Channel.Algo, "none", StringComparison.OrdinalIgnoreCase)
        ? "None"
        : string.IsNullOrWhiteSpace(Channel.KeyId) || Channel.KeyId == "0"
            ? Channel.Algo.ToUpperInvariant()
            : $"Key {Channel.KeyId}";
    public bool RxOnly => Channel.RxOnly;
    public string CardSizeText => string.Equals(Channel.CardSize, "normal", StringComparison.OrdinalIgnoreCase)
        ? "Normal"
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Channel.CardSize ?? "normal");

    public void Refresh(int number)
    {
        Number = number;
        foreach (string propertyName in new[]
                 {
                     nameof(Number), nameof(Name), nameof(System), nameof(DestinationText), nameof(ModeText),
                     nameof(SlotText), nameof(EncryptionText), nameof(RxOnly), nameof(CardSizeText)
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
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
