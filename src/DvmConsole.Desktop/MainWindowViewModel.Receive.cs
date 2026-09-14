// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Operations;
using System.ComponentModel;
using System.Diagnostics;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    private readonly object receiveLifecycleSync = new();

    internal void HandleSystemTraffic(SystemViewModel system, RadioTrafficRecord ingress)
    {
        lock (receiveLifecycleSync)
            receiveTraffic.HandleIngress(receiveSystems[system.Id].Runtime, ingress);
    }

    internal void ProcessTraffic(
        SystemViewModel system,
        FneTrafficFrame traffic,
        bool publishTrafficDiagnostics = true,
        DateTimeOffset? receivedAt = null,
        IReadOnlyList<ChannelViewModel>? preEnqueuedAudioChannels = null,
        long ingressTimestamp = 0,
        IReadOnlyList<ChannelViewModel>? preEnqueuedPatchChannels = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(traffic);
        DateTimeOffset now = receivedAt ?? DateTimeOffset.Now;
        ReceiveIngressDecision decision = receiveTraffic.ObserveIngress(
            receiveSystems[system.Id].Runtime, traffic, now, ingressTimestamp);
        system.RecordTraffic((FneTrafficFrame)decision.Traffic, publishTrafficDiagnostics);
        receiveChannelTraffic.Process(receiveSystems[system.Id].Runtime, new(
            decision,
            ReceiveDispatchTargets.From(preEnqueuedAudioChannels).ToShared(),
            ReceiveDispatchTargets.From(preEnqueuedPatchChannels).ToShared()));
    }

    private bool ExpireStaleReceiveRoutes(DateTimeOffset now)
    {
        receiveTraffic.Advance(now);
        return false;
    }

    private static string DescribeFneSignalQuality(FneTrafficFrame traffic)
    {
        // dvmhost appends one aggregate FEC error count for all three DMR
        // AMBE frames, plus positive RSSI magnitude, after the 33-byte burst
        // (network offsets 53 and 54). The aggregate must not be assigned to
        // an individual 20 ms decoder slot. Zero means the source did not
        // report that measurement.
        if (traffic.Protocol != FneTrafficProtocol.Dmr ||
            traffic.Payload.Length < DmrVoicePacketCodec.PacketBytes)
        {
            return string.Empty;
        }

        byte errors = traffic.Payload[53];
        byte rssi = traffic.Payload[54];
        string errorText = errors == 0 ? string.Empty : $", FNE BER errors {errors}/141";
        string rssiText = rssi == 0 ? string.Empty : $", RSSI -{rssi} dBm";
        return errorText + rssiText;
    }

    private Task StartAudioAsync(ChannelViewModel channel)
        => receiveOutput.StartAsync(channel, persistSelection: false);

    internal async Task ReconcileReceiveSessionsAsync(CancellationToken cancellationToken = default)
        => await receiveOutput.ReconcileAsync(cancellationToken).ConfigureAwait(false);

    internal void SetReceiveSelectionPreference(ChannelViewModel channel, bool enabled)
        => receiveOutput.SetSelectionPreference(channel, enabled);

    internal async ValueTask SetChannelReceiveEnabledAsync(
        ChannelViewModel channel,
        bool enabled,
        CancellationToken cancellationToken = default)
        => await receiveOutput.SetEnabledAsync(channel, enabled, cancellationToken).ConfigureAwait(false);

    private async Task ToggleSelectedSystemOutputMuteAsync()
    {
        if (SelectedSystem is not { } system) return;
        bool muted = receiveOutputMutePolicy.Toggle(system);
        await receiveOutput.ApplySystemMuteAsync(
            system.Name, system.Channels.Select(channel => channel.Id).ToArray(), muted).ConfigureAwait(false);
    }

    private async Task ToggleSelectedZoneOutputMuteAsync()
    {
        if (SelectedSystem?.SelectedZone is not { } zone) return;
        bool muted = receiveOutputMutePolicy.Toggle(zone);
        await receiveOutput.ApplyZoneMuteAsync(
            zone.Name, zone.Channels.Select(channel => channel.Id).ToArray(), muted).ConfigureAwait(false);
    }

    private void NotifySelectedOutputMutePresentationChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystemOutputMuted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZoneOutputMuted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystemOutputMuteGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZoneOutputMuteGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystemOutputMuteToolTip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedZoneOutputMuteToolTip)));
    }

    private void PublishReceiveDiagnostics(ChannelId channel, uint streamId, DateTimeOffset now)
        => receiveDiagnostics.Inspect(channel, streamId, now);

    private void HandleReceiveWorkItemTiming(ChannelId channel, ReceiveWorkItemTiming timing)
    {
        receiveJitterEffectiveness.Observe(channelMedia.State(channel).Runtime.Definition.SystemName, timing);
        receiveDiagnostics.ObserveTiming(channel, timing, DateTimeOffset.UtcNow);
    }

    private void PublishFinalReceiveJitterSummary(ChannelId channel, uint streamId)
        => receiveDiagnostics.Complete(channel, streamId, DateTimeOffset.UtcNow);

    private void PublishReceiveDiagnostic(ConsoleReceiveDiagnostic diagnostic)
    {
        void Publish()
        {
            if (diagnostic.ShowStatus) AudioStatusText = diagnostic.Message;
            AddDebugLog(diagnostic.Timestamp, "RX", diagnostic.Severity, diagnostic.Message);
        }
        if (uiDispatcher.CheckAccess()) Publish();
        else PostToUi(Publish);
    }



}
