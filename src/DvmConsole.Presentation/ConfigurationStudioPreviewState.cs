// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using System.Globalization;

namespace DvmConsole.Presentation;

// Owns Configuration Studio's draft card geometry and preview cache. The
// editor facade retains bindings and history orchestration, while this type
// keeps layout projection independent from field and companion editing.
internal sealed class ConfigurationStudioPreviewState
{
    private readonly IConfigurationStudioRuntimeContext runtimeContext;
    private readonly IConfigurationStudioPreviewFactory previewFactory;
    private readonly Dictionary<string, WidgetPositionSetting> savedPositions;
    private readonly Dictionary<ChannelConfiguration, WidgetPositionSetting> draftPositions = [];
    private readonly Dictionary<
        ChannelConfiguration,
        (string Signature, IConfigurationChannelPreviewViewModel Preview)> previewCache = [];

    public ConfigurationStudioPreviewState(
        IConfigurationStudioRuntimeContext runtimeContext,
        IConfigurationStudioPreviewFactory previewFactory,
        IReadOnlyDictionary<string, ConfigurationStudioPosition> initialPositions)
    {
        this.runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        this.previewFactory = previewFactory ?? throw new ArgumentNullException(nameof(previewFactory));
        ArgumentNullException.ThrowIfNull(initialPositions);
        savedPositions = initialPositions.ToDictionary(
            entry => entry.Key,
            entry => new WidgetPositionSetting { X = entry.Value.X, Y = entry.Value.Y },
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<ChannelConfiguration, WidgetPositionSetting> Positions
        => draftPositions;

    public bool LayoutChanged => draftPositions.Any(entry =>
        !savedPositions.TryGetValue(GetChannelSettingsKey(entry.Key), out WidgetPositionSetting? saved) ||
        Math.Abs(saved.X - entry.Value.X) >= 0.01 ||
        Math.Abs(saved.Y - entry.Value.Y) >= 0.01);

    public void Synchronize(IEnumerable<ZoneConfiguration> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ZoneConfiguration[] currentZones = zones.ToArray();
        HashSet<ChannelConfiguration> currentChannels = currentZones
            .SelectMany(zone => zone.Channels)
            .ToHashSet();
        foreach (ChannelConfiguration removed in draftPositions.Keys
                     .Where(channel => !currentChannels.Contains(channel))
                     .ToArray())
        {
            draftPositions.Remove(removed);
            previewCache.Remove(removed);
        }

        InitializeMissing(currentZones);
    }

    public IReadOnlyList<IConfigurationChannelPreviewViewModel> Project(ZoneConfiguration? zone)
    {
        if (zone is null)
            return [];

        HashSet<ChannelConfiguration> selectedChannels = zone.Channels.ToHashSet();
        foreach (ChannelConfiguration removed in previewCache.Keys
                     .Where(channel => !selectedChannels.Contains(channel))
                     .ToArray())
        {
            previewCache.Remove(removed);
        }

        var previews = new List<IConfigurationChannelPreviewViewModel>(zone.Channels.Count);
        foreach (ChannelConfiguration channel in zone.Channels)
        {
            WidgetPositionSetting position = draftPositions[channel];
            string signature = GetPreviewSignature(channel);
            if (!previewCache.TryGetValue(channel, out var cached) ||
                !string.Equals(cached.Signature, signature, StringComparison.Ordinal))
            {
                cached = (signature, previewFactory.Create(
                    channel,
                    position.X,
                    position.Y,
                    runtimeContext.CardHeight,
                    runtimeContext.DarkMode));
                previewCache[channel] = cached;
            }
            cached.Preview.X = position.X;
            cached.Preview.Y = position.Y;
            previews.Add(cached.Preview);
        }
        return previews;
    }

    public void Move(IConfigurationChannelPreviewViewModel preview, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(preview);
        preview.X = Math.Round(Math.Max(0, x) / 10) * 10;
        preview.Y = Math.Round(Math.Max(0, y) / 10) * 10;
        draftPositions[preview.Channel] = new WidgetPositionSetting
        {
            X = preview.X,
            Y = preview.Y
        };
    }

    public Dictionary<Guid, WidgetPositionSetting> Capture(
        Func<ChannelConfiguration, Guid> getChannelId)
    {
        ArgumentNullException.ThrowIfNull(getChannelId);
        return draftPositions.ToDictionary(
            entry => getChannelId(entry.Key),
            entry => new WidgetPositionSetting { X = entry.Value.X, Y = entry.Value.Y });
    }

    public void Restore(
        IReadOnlyDictionary<Guid, WidgetPositionSetting> positions,
        Func<Guid, ChannelConfiguration?> findChannel)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(findChannel);
        draftPositions.Clear();
        foreach (KeyValuePair<Guid, WidgetPositionSetting> entry in positions)
        {
            if (findChannel(entry.Key) is { } channel)
            {
                draftPositions[channel] = new WidgetPositionSetting
                {
                    X = entry.Value.X,
                    Y = entry.Value.Y
                };
            }
        }
        previewCache.Clear();
    }

    public void AcceptSaved()
    {
        savedPositions.Clear();
        foreach (KeyValuePair<ChannelConfiguration, WidgetPositionSetting> entry in draftPositions)
        {
            savedPositions[GetChannelSettingsKey(entry.Key)] = new WidgetPositionSetting
            {
                X = entry.Value.X,
                Y = entry.Value.Y
            };
        }
    }

    private void InitializeMissing(IEnumerable<ZoneConfiguration> zones)
    {
        foreach (ZoneConfiguration zone in zones)
        {
            double x = 0;
            double y = 0;
            foreach (ChannelConfiguration channel in zone.Channels)
            {
                double width = runtimeContext.ResolveCardWidth(channel.CardSize);
                if (x > 0 && x + width > runtimeContext.DefaultCanvasWidth)
                {
                    x = 0;
                    y += runtimeContext.CardHeight + runtimeContext.CardSpacing;
                }

                if (!draftPositions.ContainsKey(channel))
                {
                    draftPositions[channel] = savedPositions.TryGetValue(
                        GetChannelSettingsKey(channel),
                        out WidgetPositionSetting? saved)
                        ? new WidgetPositionSetting { X = saved.X, Y = saved.Y }
                        : new WidgetPositionSetting { X = x, Y = y };
                }

                x += width + runtimeContext.CardSpacing;
            }
        }
    }

    private static string GetPreviewSignature(ChannelConfiguration channel)
        => string.Join('\u001F',
            channel.Name,
            channel.System,
            channel.Tgid,
            channel.Mode,
            channel.Slot.ToString(CultureInfo.InvariantCulture),
            channel.Algo ?? string.Empty,
            channel.KeyId ?? string.Empty,
            channel.CardSize ?? string.Empty,
            channel.ResourceColor ?? string.Empty,
            channel.RxOnly.ToString(CultureInfo.InvariantCulture),
            channel.SelectableEncryption.ToString(CultureInfo.InvariantCulture));

    private static string GetChannelSettingsKey(ChannelConfiguration channel)
        => $"{channel.System}\u001F{channel.Name}";
}
