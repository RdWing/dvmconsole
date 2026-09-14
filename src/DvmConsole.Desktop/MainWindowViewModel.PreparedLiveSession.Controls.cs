// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    internal sealed partial class PreparedLiveSession
    {
        private readonly object meterSync = new();
        private IScheduledWork? meterWork;

        private ConsoleLiveChannelCommandPorts CreateCommandPorts()
        {
            var preferences = new ConsoleChannelSettingsPersistence(operationalChannels, Settings, Persist);
            return new(
                new(Runtime.TransmitChannels.CanTransmit, preferences.SaveTransmitAsync,
                    result => Presentation?.PresentChannelSelection(result)),
                new(id => !IsStopping && (Runtime.Channels[id].Operator.Snapshot.RecordingEnabled || Runtime.TransmitChannels.CanListen(id)),
                    preferences.SaveRecordingAsync, action => action(), ReconcileRecordingAsync),
                new(preferences.SaveAudioAsync), () => IsStopping);
        }

        private void InitializeControlsAndIngress()
        {
            meterWork = sessionServices!.Timers.OwnAsync("prepared-audio-meters",
                new BackgroundApplicationScheduler(failure => DesktopCrashLog.Write("Audio meter worker", failure))
                    .CreatePeriodic(TimeSpan.FromMilliseconds(ChannelAudioMeterPipeline.RefreshIntervalMilliseconds),
                        _ => { AdvanceMeters(); return ValueTask.CompletedTask; }, startImmediately: false));

        }

        private void HandleTraffic(object? sender, RadioTrafficRecord traffic)
        {
            if (!IngressSystems.TryGetValue(traffic.SystemId, out ReceiveIngressSystem? system)) return;
            lock (ReceiveSync) Runtime.Traffic.Ingress.HandleIngress(system, traffic);
        }

        private async Task ReconcileRecordingAsync(ChannelId id, bool enabled, CancellationToken token)
        {
            if (enabled)
            {
                Runtime.Receive.Work.Start(id);
                await Runtime.Receive.EnsureRecordingAudioAsync(id, token).ConfigureAwait(false);
                return;
            }
            Recordings.StopChannel(Runtime.Media.DescribeRecording(id));
            if (Runtime.Channels[id].Operator.Snapshot.AudioEnabled || !Runtime.Receive.Audio.IsActive(id)) return;
            try
            {
                await Runtime.Receive.Work.StopAsync(id).ConfigureAwait(false);
                Runtime.ReceiveDiagnostics.ResetJitter(id);
                await Runtime.Receive.Audio.StopAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (IsStopping) { }
            catch (Exception failure)
            {
                Log(Clock.UtcNow, "RX", DebugLogSeverity.Warning,
                    $"TAR decoder cleanup failed for {Runtime.Channels[id].Runtime.Definition.Name}: {failure.Message}");
            }
        }

        public void ObserveMeter(ChannelId channel, uint stream, ReadOnlySpan<short> samples,
            ChannelAudioDirection direction, TimeSpan delay = default)
        {
            lock (meterSync)
            {
                if (IsStopping) return;
                if (Runtime.Meters.Observe(channel, stream, samples, direction, delay)) meterWork?.Start();
            }
        }

        public void AdvanceMeters()
        {
            lock (meterSync)
            {
                if (IsStopping || !Runtime.Meters.HasActivity) return;
                Runtime.Meters.Advance();
                if (!Runtime.Meters.HasActivity) meterWork?.Stop();
            }
        }

    }
}
