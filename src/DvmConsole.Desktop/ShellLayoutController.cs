// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media;
using DvmConsole.Core.Settings;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

internal interface IShellLayoutSessionPort
{
    void Persist();
    void Notify(string propertyName);
    void PublishStatus(string text);
}

internal sealed class ShellLayoutSessionPort(
    Action persist,
    Action<string> notify,
    Action<string> publishStatus) : IShellLayoutSessionPort
{
    public void Persist() => persist();
    public void Notify(string propertyName) => notify(propertyName);
    public void PublishStatus(string text) => publishStatus(text);
}

internal sealed record ShellLayoutSnapshot(
    bool ShowSystemStatus,
    bool ShowChannels,
    bool ShowAlertTones,
    bool LockWidgets,
    bool ShowCallHistoryPane,
    IReadOnlyDictionary<string, WidgetPositionSetting> ChannelWidgetPositions);

/// <summary>
/// Owns shell visibility, scale, and channel-card geometry while the main view
/// model preserves the public binding surface.
/// </summary>
internal sealed class ShellLayoutController
{
    private readonly UserSettings settings;
    private readonly IReadOnlyList<ZoneViewModel> zones;
    private readonly IShellLayoutSessionPort session;

    public ShellLayoutController(
        UserSettings settings,
        IReadOnlyList<ZoneViewModel> zones,
        IShellLayoutSessionPort session)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.zones = zones ?? throw new ArgumentNullException(nameof(zones));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        ScaleTransform = new ScaleTransform
        {
            ScaleX = settings.UiScale,
            ScaleY = settings.UiScale
        };
    }

    public ScaleTransform ScaleTransform { get; }

    public bool ShowCallHistoryPane
    {
        get => settings.ShowCallHistoryPane;
        set => Set(
            settings.ShowCallHistoryPane,
            value,
            updated => settings.ShowCallHistoryPane = updated,
            nameof(MainWindowViewModel.ShowCallHistoryPane),
            nameof(MainWindowViewModel.IsActivitySidebarCollapsed),
            nameof(MainWindowViewModel.ActivitySidebarWidth));
    }

    public bool ShowSystemStatus
    {
        get => settings.ShowSystemStatus;
        set => Set(
            settings.ShowSystemStatus,
            value,
            updated => settings.ShowSystemStatus = updated,
            nameof(MainWindowViewModel.ShowSystemStatus));
    }

    public bool ShowChannels
    {
        get => settings.ShowChannels;
        set => Set(
            settings.ShowChannels,
            value,
            updated => settings.ShowChannels = updated,
            nameof(MainWindowViewModel.ShowChannels));
    }

    public bool ShowAlertTones
    {
        get => settings.ShowAlertTones;
        set => Set(
            settings.ShowAlertTones,
            value,
            updated => settings.ShowAlertTones = updated,
            nameof(MainWindowViewModel.ShowAlertTones));
    }

    public bool LockWidgets
    {
        get => settings.LockWidgets;
        set => Set(
            settings.LockWidgets,
            value,
            updated => settings.LockWidgets = updated,
            nameof(MainWindowViewModel.LockWidgets),
            nameof(MainWindowViewModel.CanResizeLayout));
    }

    public double UiFontSize
    {
        get => settings.UiFontSize;
        set
        {
            double normalized = Math.Clamp(value, 11, 20);
            if (Math.Abs(settings.UiFontSize - normalized) < 0.001)
                return;
            settings.UiFontSize = normalized;
            session.Persist();
            Notify(
                nameof(MainWindowViewModel.UiFontSize),
                nameof(MainWindowViewModel.UiFontSizeText),
                nameof(MainWindowViewModel.UiSmallFontSize),
                nameof(MainWindowViewModel.UiCompactFontSize),
                nameof(MainWindowViewModel.UiHeadingFontSize),
                nameof(MainWindowViewModel.ChannelCardHeight));
            if (settings.ChannelWidgetPositions.Count == 0)
                ApplyDefaultChannelWidgetLayout();
            foreach (ZoneViewModel zone in zones)
            {
                zone.SetWidgetCardHeight(ChannelCardHeight);
                zone.RefreshWidgetCanvasBounds();
            }
        }
    }

    public double UiScale
    {
        get => settings.UiScale;
        set
        {
            double normalized = Math.Clamp(value, 0.75, 1.5);
            if (Math.Abs(settings.UiScale - normalized) < 0.001)
                return;
            settings.UiScale = normalized;
            ScaleTransform.ScaleX = normalized;
            ScaleTransform.ScaleY = normalized;
            session.Persist();
            Notify(nameof(MainWindowViewModel.UiScale), nameof(MainWindowViewModel.UiScaleText));
        }
    }

    public double ChannelCardHeight => 122 + ((UiFontSize - 14) * 3);

    public ShellLayoutSnapshot Capture()
        => new(
            settings.ShowSystemStatus,
            settings.ShowChannels,
            settings.ShowAlertTones,
            settings.LockWidgets,
            settings.ShowCallHistoryPane,
            settings.ChannelWidgetPositions.ToDictionary(
                entry => entry.Key,
                entry => new WidgetPositionSetting
                {
                    X = entry.Value.X,
                    Y = entry.Value.Y
                },
                StringComparer.OrdinalIgnoreCase));

    public void Reset()
    {
        settings.ShowSystemStatus = true;
        settings.ShowChannels = true;
        settings.ShowAlertTones = true;
        settings.LockWidgets = true;
        settings.ShowCallHistoryPane = true;
        settings.ChannelWidgetPositions.Clear();
        ApplyDefaultChannelWidgetLayout();
        session.Persist();
        Notify(
            nameof(MainWindowViewModel.ShowSystemStatus),
            nameof(MainWindowViewModel.ShowChannels),
            nameof(MainWindowViewModel.ShowAlertTones),
            nameof(MainWindowViewModel.LockWidgets),
            nameof(MainWindowViewModel.CanResizeLayout),
            nameof(MainWindowViewModel.ShowCallHistoryPane),
            nameof(MainWindowViewModel.IsActivitySidebarCollapsed),
            nameof(MainWindowViewModel.ActivitySidebarWidth));
        session.PublishStatus("Channel widgets reset to their default positions and locked.");
    }

    public void Restore(ShellLayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        settings.ShowSystemStatus = snapshot.ShowSystemStatus;
        settings.ShowChannels = snapshot.ShowChannels;
        settings.ShowAlertTones = snapshot.ShowAlertTones;
        settings.LockWidgets = snapshot.LockWidgets;
        settings.ShowCallHistoryPane = snapshot.ShowCallHistoryPane;
        settings.ChannelWidgetPositions = snapshot.ChannelWidgetPositions.ToDictionary(
            entry => entry.Key,
            entry => new WidgetPositionSetting
            {
                X = entry.Value.X,
                Y = entry.Value.Y
            },
            StringComparer.OrdinalIgnoreCase);
        RestoreChannelWidgetLayout();
        session.Persist();
        Notify(
            nameof(MainWindowViewModel.ShowSystemStatus),
            nameof(MainWindowViewModel.ShowChannels),
            nameof(MainWindowViewModel.ShowAlertTones),
            nameof(MainWindowViewModel.LockWidgets),
            nameof(MainWindowViewModel.CanResizeLayout),
            nameof(MainWindowViewModel.ShowCallHistoryPane),
            nameof(MainWindowViewModel.IsActivitySidebarCollapsed),
            nameof(MainWindowViewModel.ActivitySidebarWidth));
        session.PublishStatus("Previous channel widget layout restored.");
    }

    public void MoveChannelWidget(ChannelViewModel channel, double x, double y, bool persistMove)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (settings.LockWidgets)
            return;

        channel.SetWidgetPosition(x, y, isFinal: persistMove);
        if (!persistMove)
            return;

        settings.ChannelWidgetPositions[channel.SettingsKey] = new WidgetPositionSetting
        {
            X = channel.WidgetX,
            Y = channel.WidgetY
        };
        session.Persist();
        session.PublishStatus($"Moved {channel.Name} to {channel.WidgetX:0}, {channel.WidgetY:0}.");
    }

    public void MoveWebStreamWidget(WebStreamViewModel stream, double x, double y, bool persistMove)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (settings.LockWidgets)
            return;

        stream.SetWidgetPosition(x, y, isFinal: persistMove);
        if (!persistMove)
            return;

        settings.ChannelWidgetPositions[stream.SettingsKey] = new WidgetPositionSetting
        {
            X = stream.WidgetX,
            Y = stream.WidgetY
        };
        session.Persist();
        session.PublishStatus($"Moved {stream.Name} to {stream.WidgetX:0}, {stream.WidgetY:0}.");
    }

    public void RestoreChannelWidgetLayout()
    {
        ApplyDefaultChannelWidgetLayout();
        foreach (ChannelViewModel channel in zones.SelectMany(zone => zone.Channels).Distinct())
        {
            if (settings.ChannelWidgetPositions.TryGetValue(
                    channel.SettingsKey,
                    out WidgetPositionSetting? position))
            {
                channel.SetWidgetPosition(position.X, position.Y);
            }
        }
        foreach (WebStreamViewModel stream in zones.SelectMany(zone => zone.WebStreams).Distinct())
        {
            if (settings.ChannelWidgetPositions.TryGetValue(
                    stream.SettingsKey,
                    out WidgetPositionSetting? position))
            {
                stream.SetWidgetPosition(position.X, position.Y);
            }
        }
    }

    private void ApplyDefaultChannelWidgetLayout()
    {
        foreach (ZoneViewModel zone in zones)
        {
            double x = 0;
            double y = 0;
            void Place(double width, Action<double, double> setPosition)
            {
                if (x > 0 && x + width > MainWindowViewModel.DefaultWidgetCanvasWidth)
                {
                    x = 0;
                    y += ChannelCardHeight + MainWindowViewModel.ChannelWidgetSpacing;
                }

                setPosition(x, y);
                x += width + MainWindowViewModel.ChannelWidgetSpacing;
            }

            foreach (ChannelViewModel channel in zone.Channels)
                Place(channel.CardWidth, (left, top) => channel.SetWidgetPosition(left, top));
            foreach (WebStreamViewModel stream in zone.WebStreams)
                Place(stream.CardWidth, (left, top) => stream.SetWidgetPosition(left, top));
        }
    }

    private void Set(
        bool current,
        bool value,
        Action<bool> assign,
        params string[] propertyNames)
    {
        if (current == value)
            return;
        assign(value);
        session.Persist();
        Notify(propertyNames);
    }

    private void Notify(params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
            session.Notify(propertyName);
    }
}
