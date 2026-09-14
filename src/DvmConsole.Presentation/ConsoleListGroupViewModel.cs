// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;

namespace DvmConsole.Presentation;

public interface IConsoleListRow
{
    string Name { get; }
}

public sealed class ConsoleListGroupViewModel(
    string name, bool isSystem, IReadOnlyList<IConsoleListRow> children)
    : IConsoleListRow, INotifyPropertyChanged
{
    private readonly List<IConsoleListRow> children = children.ToList();

    internal void RestoreOrder(IReadOnlyDictionary<string, int> ranks)
    {
        var ordered = children.OrderBy(child => child is ChannelListItemViewModel item
            ? ranks.GetValueOrDefault(item.Id.ToString(), int.MaxValue) : -1).ToArray();
        children.Clear();
        children.AddRange(ordered);
        foreach (var group in children.OfType<ConsoleListGroupViewModel>()) group.RestoreOrder(ranks);
    }

    public DvmConsole.Application.SystemId? SystemId { get; init; }
    public string ConnectionText { get; private set; } = string.Empty;
    public void SetConnectionText(string text)
    {
        if (ConnectionText == text) return;
        ConnectionText = text;
        PropertyChanged?.Invoke(this, new(nameof(IsConnected)));
        PropertyChanged?.Invoke(this, new(nameof(IsConnecting)));
        PropertyChanged?.Invoke(this, new(nameof(IsDisconnected)));
        PropertyChanged?.Invoke(this, new(nameof(ConnectionText)));
        PropertyChanged?.Invoke(this, new(nameof(ConnectionActionText)));
    }
    public bool IsConnected => ConnectionText == "Connected";
    public bool IsConnecting => ConnectionText is "Connecting…" or "Disconnecting…";
    public bool IsDisconnected => ConnectionText is "Disconnected" or "Failed" or "Offline";
    public bool CanToggleConnection { get; private set; }
    public string ConnectionActionText => $"{(ConnectionText is "Connected" or "Connecting…" ? "Disconnect" : "Connect")} {Name}";
    public void SetConnectionEnabled(bool enabled)
    {
        if (CanToggleConnection == enabled) return;
        CanToggleConnection = enabled;
        PropertyChanged?.Invoke(this, new(nameof(CanToggleConnection)));
    }
    private readonly HashSet<DvmConsole.Application.ChannelId> receivingChannels = [];
    public bool ShowReceiveActivity { get; init; } = true;
    private bool HasZones => children.Any(child => child is ConsoleListGroupViewModel);
    public bool IsReceiving => receivingChannels.Count > 0;
    public bool ShowReceiveDot => ShowReceiveActivity && IsReceiving;
    public bool ShowReceiveNames => ShowReceiveDot && (!IsSystem || !HasZones || !IsExpanded);
    public string ReceiveActivityText { get; private set; } = string.Empty;

    internal void UpdateReceiveActivity(DvmConsole.Application.ChannelId id, bool receiving)
    {
        bool changed = receiving ? receivingChannels.Add(id) : receivingChannels.Remove(id);
        if (!changed) return;
        // Use topology order rather than call arrival order; refresh only on activity transitions.
        ReceiveActivityText = string.Join(", ", AllChannels()
            .Where(channel => receivingChannels.Contains(channel.Id)).Select(channel => channel.Name));
        PropertyChanged?.Invoke(this, new(nameof(IsReceiving)));
        PropertyChanged?.Invoke(this, new(nameof(ShowReceiveDot)));
        PropertyChanged?.Invoke(this, new(nameof(ShowReceiveNames)));
        PropertyChanged?.Invoke(this, new(nameof(ReceiveActivityText)));
        PropertyChanged?.Invoke(this, new(nameof(AccessibilityText)));
    }

    private IEnumerable<ChannelListItemViewModel> AllChannels()
    {
        foreach (var child in children)
        {
            if (child is ChannelListItemViewModel channel) yield return channel;
            else if (child is ConsoleListGroupViewModel group)
                foreach (var descendant in group.AllChannels()) yield return descendant;
        }
    }
    public string Name { get; } = name;
    public bool IsSystem { get; } = isSystem;
    public bool IsExpanded { get; private set; } = true;
    public string DisclosureText => IsExpanded ? "▾" : "▸";
    public string AccessibilityText => $"{(IsExpanded ? "Collapse" : "Expand")} {(IsSystem ? "FNE" : "zone")} {Name}{(IsReceiving ? $", receiving {ReceiveActivityText}" : string.Empty)}";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
        PropertyChanged?.Invoke(this, new(nameof(ShowReceiveNames)));
        PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
        PropertyChanged?.Invoke(this, new(nameof(DisclosureText)));
        PropertyChanged?.Invoke(this, new(nameof(AccessibilityText)));
    }

    internal bool Contains(ChannelListItemViewModel item)
        => children.Any(child => ReferenceEquals(child, item) ||
            child is ConsoleListGroupViewModel group && group.Contains(item));

    internal IEnumerable<IConsoleListRow> VisibleDescendants()
    {
        if (!IsExpanded) yield break;
        foreach (IConsoleListRow child in children)
        {
            yield return child;
            if (child is ConsoleListGroupViewModel group)
                foreach (IConsoleListRow descendant in group.VisibleDescendants())
                    yield return descendant;
        }
    }
}
