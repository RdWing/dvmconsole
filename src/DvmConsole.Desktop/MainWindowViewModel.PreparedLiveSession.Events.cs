// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using DvmConsole.Media;
using System.Globalization;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    internal sealed partial class PreparedLiveSession : IConsoleRadioLifecyclePort
    {
        private IReadOnlyDictionary<SystemId, IRadioSession> radiosById = null!;
        private ConsoleRadioLifecycleController radioLifecycle => Runtime.RadioLifecycle;
        private IReadOnlyDictionary<SystemId, string> preparedSystemEndpoints = new Dictionary<SystemId, string>();
        private readonly Dictionary<SystemId, P25KeyRequestPort> keyRequestPorts = [];
        public P25KeyRetrievalCoordinator? KeyRetrieval { get; private set; }
        public SingleFlightAsyncAction? ReceiveReconciler { get; private set; }
        public ConsoleSubscriberCommandDispatcher SubscriberCommands { get; } = new(SystemClock.Instance, historyLimit: 50);
        private bool IsRetired => State!.Terminal.IsClosed;

        private ConsoleLiveRadioLifecyclePorts PrepareLifecycle(IReadOnlyList<IRadioSession> radios, P25KeyRing? keys)
        {
            radiosById = radios.ToDictionary(radio => radio.SystemId);
            foreach (IRadioSession radio in radios)
            {
                Action<byte, ushort>? send = radio is IRadioP25KeyEndpoint endpoint ? endpoint.RequestP25Key
                    : radio is IFneRadioSession legacy ? legacy.RequestP25Key : null;
                if (send is null) continue;
                keyRequestPorts.Add(radio.SystemId, new(State!.KeyRequests[radio.SystemId], send,
                    (algorithm, key, retry) => Log(Clock.UtcNow, radio.Name, DebugLogSeverity.Debug,
                        $"P25 KMM KEY_REQ {(retry ? "retry " : string.Empty)}sent; algId = 0x{algorithm:X2}, kID = 0x{key:X4}.")));
            }
            KeyRetrieval = keys is null ? null : new(keys);
            return new(State!, radiosById, KeyRetrieval, keyRequestPorts, SubscriberCommands, this);
        }

        private void InitializeLifecycleMaintenance()
        {
            ReceiveReconciler = new(Runtime.Receive.Output.ReconcileAsync,
                failure => DesktopCrashLog.Write("Receive reconciliation", failure));
            if (dependencies.NetworkDisabledDemo) return;
            var maintenance = sessionServices!.Timers.OwnAsync("receive-maintenance",
                new BackgroundApplicationScheduler(failure => DesktopCrashLog.Write("Receive maintenance", failure))
                    .CreatePeriodic(TimeSpan.FromSeconds(1), token =>
                    {
                        if (!token.IsCancellationRequested && !IsRetired)
                        {
                            AdvanceMaintenance(Clock.UtcNow);
                            ReceiveReconciler.Request();
                        }
                        return ValueTask.CompletedTask;
                    }, startImmediately: false));
            maintenance.Start();
        }

        public void AdvanceMaintenance(DateTimeOffset now)
        {
            if (IsRetired) return;
            lock (ReceiveSync)
            {
                Runtime.Traffic.Ingress.Advance(now);
                if (Runtime.EpisodeRetirement.Advance(now)) HistoryChanged();
            }
            SubscriberCommands.Expire();
        }

        private void HandleAuthorityChanged(object? sender, TalkgroupAuthorityRecord authority)
            => radioLifecycle.ApplyAuthority(authority);
        public void ApplyAuthority(TalkgroupAuthorityRecord authority) => radioLifecycle.ApplyAuthority(authority);
        private void HandleConnectionChanged(object? sender, RadioConnectionSnapshot snapshot)
            => radioLifecycle.OnConnection(sender, snapshot);
        public void ApplyConnection(RadioConnectionSnapshot snapshot) => radioLifecycle.ApplyConnection(snapshot);
        private void HandleKeyReceived(object? sender, RadioP25KeyResponse response) => radioLifecycle.OnKey(sender, response);
        public void ApplyKey(RadioP25KeyResponse response) => radioLifecycle.ApplyKey(response);
        private void HandleSubscriberAcknowledged(object? sender, ConsoleSubscriberAcknowledgement response)
            => radioLifecycle.OnSubscriberAcknowledged(sender, response);

        bool IConsoleRadioLifecyclePort.IsStopping => IsRetired;
        void IConsoleRadioLifecyclePort.ChannelChanged(ChannelId id)
        {
            // Snapshot subscriptions already observe the authoritative channel change.
        }
        void IConsoleRadioLifecyclePort.PublishStatus(string text) => Status.SetConsole(text);

        void IConsoleRadioLifecyclePort.AuthorityChanged(TalkgroupAuthorityRecord authority,
            IReadOnlyList<ChannelId> unavailable, int stoppedPatches)
        {
            if (unavailable.Count == 0) return;
            if (Runtime.TransmitChannels.HasStartingTransmit(unavailable)) Runtime.Transmit.Manual.CancelStartup();
            ChannelId[] active = Runtime.Transmit.Microphone.ActiveChannels.ToArray();
            bool stopManual = active.Any(unavailable.Contains);
            string channels = string.Join(", ", unavailable.Select(id => DescribeUnavailableTalkgroup(Runtime.Channels[id].Runtime.Definition)));
            string stopped = stopManual || stoppedPatches > 0 ? " Active transmission stopped." : string.Empty;
            string message = $"{radiosById[authority.SystemId].Name}: FNE talkgroup table does not allow {channels}; PTT disabled.{stopped}";
            Status.SetConsole(message);
            Status.SetTransmit(message);
            Log(Clock.UtcNow, "TX", DebugLogSeverity.Warning, message);
            if (stopManual) TaskObservation.Observe(Runtime.Transmit.Manual.StopAsync(active, message));
        }

        void IConsoleRadioLifecyclePort.ConnectionChanged(IRadioSession radio,
            RadioConnectionSnapshot snapshot, ConsoleConnectionChange transition)
        {
            ReceiveIngressSystem system = IngressSystems[snapshot.SystemId];
            if (snapshot.State != RadioConnectionState.Connected)
            {
                ChannelId[] ids = system.Channels.Select(channel => channel.Id).ToArray();
                if (Runtime.TransmitChannels.HasStartingTransmit(ids)) Runtime.Transmit.Manual.CancelStartup();
                if (Runtime.TransmitChannels.OwnsActiveTransmit(ids, Runtime.Transmit.Microphone.ActiveChannels))
                    TaskObservation.Observe(Runtime.Transmit.StopForDisconnectedSystemAsync(ids, Runtime.TransmitChannels,
                        PttStateChangeLock, TransmitAdmissionGate, () => Presentation?.ClearDisconnectedTransmitLatches(),
                        State!.ManualTransmitOwnership.Clear,
                        $"Transmission stopped because {system.Name} is {snapshot.State.ToString().ToLowerInvariant()}."));
            }
            if (snapshot.State == RadioConnectionState.Connected) ReceiveReconciler?.Request();
            if (transition.LostConnection && KeyRetrieval is not null) TaskObservation.Observe(SynchronizePatchSourcesAsync());
            Status.SetConsole($"{system.Name}: {snapshot.State} — {snapshot.Message}");
            ConsoleCallHistoryRecord? history = transition.Changed && snapshot.State is
                RadioConnectionState.Connected or RadioConnectionState.Disconnected or RadioConnectionState.Faulted
                ? RecordConnectionEvent(snapshot) : null;
            if (Presentation is { } view && view.Systems.FirstOrDefault(candidate => candidate.Id == system.Id) is { } systemView)
                view.PresentSystemStatus(systemView, new FneConnectionStatus(system.Name,
                    FneConnectionStateMapper.ToTransportState(snapshot.State), snapshot.Message, snapshot.ChangedAt), transition, history);
        }

        void IConsoleRadioLifecyclePort.KeyStateChanged(IRadioSession radio)
        {
            if (Presentation is { } view) view.PostToUi(view.RefreshP25KeyState);
        }

        void IConsoleRadioLifecyclePort.KeyReceived(IRadioSession radio, RadioP25KeyResponse response,
            bool accepted, string message)
        {
            if (!radio.IsConnected) return;
            if (accepted) TaskObservation.Observe(SynchronizePatchSourcesAsync());
            Status.SetConsole(message);
            if (accepted && Presentation is { } view) view.PostToUi(view.RefreshP25KeyState);
        }

        private ConsoleCallHistoryRecord RecordConnectionEvent(RadioConnectionSnapshot snapshot)
        {
            string message = $"{snapshot.Name} {snapshot.State.ToString().ToLowerInvariant()}";
            string endpoint = preparedSystemEndpoints.GetValueOrDefault(snapshot.SystemId)
                ?? Presentation?.Systems.FirstOrDefault(system => system.Id == snapshot.SystemId)?.Endpoint ?? string.Empty;
            var record = new ConsoleCallHistoryRecord(CallId.New(), Clock.UtcNow.ToLocalTime(), null,
                SystemId.FromName("FNE"), "FNE", null, "FNE", RadioMediaProtocol.Dmr, 0, 0, 0, [], null,
                message, ConsoleCallDirection.Event, new(false, false, null, null), "FNE", message,
                radiosById[snapshot.SystemId].SourceId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, endpoint);
            State!.History.Add(record);
            return record;
        }

        private void ProjectPreparedHistory()
        {
            if (Presentation is not { } view) return;
            view.PostToUi(() =>
            {
                if (IsRetired) return;
                foreach (ConsoleCallHistoryRecord record in State!.History.Snapshot)
                    view.callHistory.ProjectRuntimeRecord(record);
                view.NotifyCallHistoryChanged();
            });
        }

    }
}
