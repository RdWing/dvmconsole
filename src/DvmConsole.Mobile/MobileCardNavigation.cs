// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

/// <summary>Session-local FNE and zone selection for tablet cards; never changes radio state.</summary>
internal sealed class MobileCardNavigation : StackPanel
{
    private readonly ConsoleTopologySnapshot topology;
    private readonly IReadOnlyDictionary<ChannelId, Control> cards;
    private readonly Dictionary<SystemId, Tab> systems = [];
    private readonly Dictionary<(SystemId, ZoneId), Tab> zones = [];
    private readonly Dictionary<SystemId, ZoneId> selectedZones = [];
    private readonly StackPanel zoneRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private SystemId? selectedSystem;
    private readonly HashSet<SystemId> activeSystems = [];
    private readonly HashSet<(SystemId, ZoneId)> activeZones = [];

    public MobileCardNavigation(ConsoleTopologySnapshot topology, IReadOnlyDictionary<ChannelId, Control> cards)
    {
        this.topology = topology;
        this.cards = cards;
        Spacing = 4;
        Margin = new Thickness(16, 4, 16, 4);
        var systemRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Children.Add(Scroller(systemRow));
        Children.Add(Scroller(zoneRow));
        int systemIndex = 0;
        foreach (var system in topology.Systems)
        {
            var tab = new Tab(system.Name, "FNE", system.Color ?? DvmConsole.Presentation.ConsoleSystemColors.At(systemIndex++));
            tab.Button.Click += (_, _) => SelectSystem(system.Id);
            systems.Add(system.Id, tab);
            systemRow.Children.Add(tab.View);
            foreach (var zone in topology.Zones.Where(zone => topology.Channels.Any(channel =>
                channel.SystemId == system.Id && channel.ZoneId == zone.Id)))
            {
                var zoneTab = new Tab(zone.Name, "Zone", zone.Color);
                zoneTab.Button.Click += (_, _) => SelectZone(system.Id, zone.Id);
                zones.Add((system.Id, zone.Id), zoneTab);
                selectedZones.TryAdd(system.Id, zone.Id);
            }
        }
        if (topology.Systems.Count > 0) SelectSystem(topology.Systems[0].Id);
    }

    public void Reveal(ChannelId id)
    {
        var channel = topology.Channels.FirstOrDefault(channel => channel.Id == id);
        if (channel is not null) SelectZone(channel.SystemId, channel.ZoneId);
    }

    public void RefreshActivity(ConsoleRuntimeSnapshot snapshot)
    {
        activeSystems.Clear();
        activeZones.Clear();
        foreach (var channel in topology.Channels)
            if (snapshot.Channels.TryGetValue(channel.Id, out var state) && state.ReceiveEnabled && state.ReceiveActive)
            {
                activeSystems.Add(channel.SystemId);
                activeZones.Add((channel.SystemId, channel.ZoneId));
            }
        foreach (var (id, tab) in systems) tab.SetReceiving(activeSystems.Contains(id));
        foreach (var (id, tab) in zones) tab.SetReceiving(activeZones.Contains(id));
    }

    private void SelectSystem(SystemId system)
    {
        if (selectedSystem != system)
        {
            selectedSystem = system;
            zoneRow.Children.Clear();
            foreach (var (id, tab) in zones)
                if (id.Item1 == system) zoneRow.Children.Add(tab.View);
        }
        UpdateSelection();
    }

    private void SelectZone(SystemId system, ZoneId zone)
    {
        selectedZones[system] = zone;
        SelectSystem(system);
    }

    internal void RefreshSelection() => UpdateSelection();

    private void UpdateSelection()
    {
        foreach (var (id, tab) in systems) tab.Button.IsChecked = id == selectedSystem;
        foreach (var (id, tab) in zones)
            tab.Button.IsChecked = id.Item1 == selectedSystem && selectedZones.GetValueOrDefault(id.Item1) == id.Item2;
        foreach (var channel in topology.Channels)
            if (cards.TryGetValue(channel.Id, out var card))
                card.IsVisible = channel.SystemId == selectedSystem &&
                    selectedZones.GetValueOrDefault(channel.SystemId) == channel.ZoneId;
    }

    private static ScrollViewer Scroller(Control content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private sealed class Tab
    {
        private readonly string name;
        private readonly string kind;
        private readonly Border activity = new()
        {
            Height = 8,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0, 0, 0, 2),
            Opacity = 0
        };
        private bool receiving;
        public ToggleButton Button { get; }
        public StackPanel View { get; }

        public Tab(string name, string kind, string? color)
        {
            this.name = name;
            this.kind = kind;
            Button = new ToggleButton
            {
                Content = name,
                MinHeight = 44,
                Padding = new Thickness(10, 4),
                FontWeight = FontWeight.SemiBold,
                CornerRadius = new CornerRadius(8),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Button.Bind(TemplatedControl.FontSizeProperty, Button.GetResourceObservable("MobileNavigationFontSize"));
            var accent = Color.TryParse(color, out var configured) ? new SolidColorBrush(configured) :
                new SolidColorBrush(Color.Parse("#2F81F7"));
            // Own the small tab face instead of fighting the theme's checked-blue
            // template. Codeplug text colors belong to their original filled tabs;
            // this transparent treatment uses the app's readable theme text.
            var label = new TextBlock
            {
                Text = name,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.Bind(TextBlock.ForegroundProperty, Button.GetResourceObservable("PrimaryTextBrush"));
            label.Bind(TextBlock.FontSizeProperty, Button.GetResourceObservable("MobileNavigationFontSize"));
            // Keep receive activity under the label, between the tab accents.
            // Opacity preserves its slot when idle, so neither label nor tab moves.
            var labelContent = new StackPanel
            {
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { label, activity }
            };
            var face = new Border
            {
                Padding = new Thickness(10, 4),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Child = labelContent
            };
            Button.Template = new Avalonia.Controls.Templates.FuncControlTemplate<ToggleButton>((_, _) => face);
            void RefreshSelection()
            {
                bool selected = Button.IsChecked == true;
                face.BorderBrush = accent;
                // Selection mirrors the curved bottom edge at the top; sides stay open.
                face.BorderThickness = selected ? new Thickness(0, 2, 0, 2) : new Thickness(0, 0, 0, 2);
                // Reserve the top accent's space even when unselected.
                face.Padding = selected ? new Thickness(12, 4, 12, 4) : new Thickness(12, 6, 12, 4);
            }
            Button.PropertyChanged += (_, args) =>
            {
                if (args.Property == ToggleButton.IsCheckedProperty) RefreshSelection();
            };
            Button.ActualThemeVariantChanged += (_, _) => RefreshSelection();
            RefreshSelection();
            activity.BorderBrush = new SolidColorBrush(Color.Parse("#3BCB88"));
            AutomationProperties.SetName(Button, $"{kind} {name}");
            View = new StackPanel { Children = { Button } };
        }

        public void SetReceiving(bool value)
        {
            if (receiving == value) return;
            receiving = value;
            activity.Opacity = value ? 1 : 0;
            AutomationProperties.SetName(Button, $"{kind} {name}{(value ? ", receiving" : string.Empty)}");
        }
    }
}
