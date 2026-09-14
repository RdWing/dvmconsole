// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Groups restoration and event-wiring phases that configure an already
// composed console session. Keeping these phases explicit makes the shell
// constructor read as an ordered composition workflow.
public sealed partial class MainWindowViewModel
{
    private void RestoreToolbarClocks()
    {
        List<ToolbarClockSetting> configuredClocks = (userSettings.ToolbarClocks ?? [])
            .Take(UserSettings.MaximumToolbarClocks)
            .ToList();
        while (configuredClocks.Count < UserSettings.MaximumToolbarClocks)
            configuredClocks.Add(new ToolbarClockSetting());
        for (int index = 0; index < configuredClocks.Count; index++)
            toolbarClocks.Add(new ToolbarClockViewModel(index + 1, configuredClocks[index]));
        RefreshClock();
    }

    private void RestoreChannelPresentation()
    {
        RestoreChannelWidgetLayout();
        foreach (ZoneViewModel zone in Zones)
        {
            zone.SetWidgetCardHeight(ChannelCardHeight);
            zone.SetDarkMode(userSettings.DarkMode);
        }
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels).Distinct())
            channel.SetDarkMode(userSettings.DarkMode);
    }

    private void ConfigureWebStreams()
        => webStreamOperator.Initialize(
            Zones.SelectMany(zone => zone.WebStreams),
            AudioOutputDevices);

    private void ConfigureChannels()
    {
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
        {
            channel.ConfigureStatePresentation(uiDispatcher);
            channel.SelectionChanged += HandleChannelSelectionChanged;
            channel.SetOutputDeviceOptions(AudioOutputDevices);
            channel.TransmitEncryptionChanged += HandleChannelEncryptionChanged;
            channel.RecordingStateChanged += HandleChannelRecordingChanged;
            channel.VolumeChanged += HandleChannelVolumeChanged;
            channel.StereoBalanceChanged += HandleChannelStereoBalanceChanged;
            channel.PropertyChanged += HandleActivityChannelPropertyChanged;
            channel.PropertyChanged += HandleToneTargetChannelPropertyChanged;
            channel.IgnoredSubscriberIdsText = string.Join(", ", channel.SessionState.RecordingSubscribers.IgnoredSubscribers);
            channel.ConfigureAudio(
                candidate => ChangeChannelReceiveSelectionAsync(candidate, enabled: true),
                candidate => ChangeChannelReceiveSelectionAsync(candidate, enabled: false));
            channel.ConfigureTransmit(StartTransmitAsync, StopTransmitAsync);
            channel.ConfigureEncryptionCommand(encrypted => SetChannelTransmitEncryptedAsync(channel.Id, encrypted).AsTask());
            channel.ConfigureRecordingCommand(enabled => SetChannelRecordingEnabledAsync(channel.Id, enabled).AsTask());
            if (channel.IsRecordingEnabled)
                TaskObservation.Observe(EnsureRecordingAudioAsync(channel));
        }
    }

    private void SubscribeToSystems()
    {
        foreach (SystemViewModel system in Systems)
        {
            system.JitterBufferChanged += HandleSystemJitterBufferChanged;
            system.PropertyChanged += HandleSystemPropertyChanged;
            // Legacy/custom radio adapters without portable notifications keep
            // the same compatibility path; production FNE events use shared ingress.
            if (system.RadioSession is not IRadioConnectionStateNotifications)
                system.StatusChanged += HandleSubscribedSystemStatus;
            system.LogReceived += HandleSystemLog;
            if (system.RadioSession is not IRadioP25KeyEndpoint)
                system.KeyResponseReceived += HandleSystemKeyResponse;
        }
    }

    private static string DescribeUnavailableTalkgroup(ChannelRuntimeDefinition channel)
        => channel.Protocol == ChannelProtocol.Dmr
            ? $"{channel.Name} (TG {channel.DestinationId}, TS{channel.Slot + 1})"
            : $"{channel.Name} (TG {channel.DestinationId}, {channel.Mode.ToUpperInvariant()})";

    private void HandleSubscribedSystemStatus(object? sender, FneConnectionStatus status)
    {
        if (sender is SystemViewModel system)
            HandleSystemStatus(system, status);
    }

    private void RestoreInitialSelection()
        => channelSelection.Restore();
}
