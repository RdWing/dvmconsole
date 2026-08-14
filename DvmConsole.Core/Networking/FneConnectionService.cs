// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2024 Caleb, K4PHP
*   Copyright (C) 2025 Steven Jennison, KD8RHO
*   Copyright (C) 2026 C. Lovell, Dev_Ranger
*
*/
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using dvmconsole;

namespace DvmConsole.Core.Networking
{
    /// <summary>
    /// Headless FNE connection service: one transport per configured
    /// system, with start/stop/restart lifecycle and a deterministic
    /// keepalive heartbeat driven through an injectable one-shot
    /// scheduler. The scheduler seam keeps every timing behavior
    /// testable without real time; when none is injected a real
    /// one-shot <see cref="Timer"/>-based scheduler is used with a
    /// 5-second heartbeat (fnecore parity PingTime=5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thread-safety: every state transition for a system is serialized
    /// on that system's own lock, so concurrent Start/Stop/Restart calls
    /// on the same system are safe (the busy guard makes a racing Stop
    /// a no-op while a Start is in flight). Transport events may be
    /// raised from any thread; the handlers take the same per-system
    /// lock. <see cref="Dispose"/> idempotently tears down every system
    /// and publishes nothing.
    /// </para>
    /// </remarks>
    public sealed class FneConnectionService : IFneConnectionService
    {
        /// <summary>
        /// Heartbeat period used with the default scheduler (fnecore
        /// maintenance ping loop parity PingTime=5).
        /// </summary>
        private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Delay held across a restart before reconnecting (WPF parity
        /// <c>Task.Delay(250)</c>, MainWindow.FneConnections.cs:155).
        /// </summary>
        private static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Per-system connection state. Every member is guarded by
        /// <see cref="Sync"/>.
        /// </summary>
        private sealed class ConnectionState
        {
            public readonly object Sync = new object();

            public string SystemName = string.Empty;
            public Codeplug.System? SystemConfig;
            public IFneTransport? Transport;
            public bool IsConnected;
            public bool IsBusy;
            public bool IsStarted;
            public int MissedPings;
            public bool AckObserved;
            public IDisposable? HeartbeatHandle;
            public IDisposable? RestartHandle;
            public Action? PeerConnectedHandler;
            public Action? PeerDisconnectedHandler;
            public Action? PingAcknowledgedHandler;
        }

        /// <summary>
        /// Cancels a one-shot scheduler handle by disposing its timer.
        /// </summary>
        private sealed class TimerCancellation : IDisposable
        {
            private readonly Timer timer;

            public TimerCancellation(Timer timer) => this.timer = timer;

            public void Dispose() => timer.Dispose();
        }

        private readonly Dictionary<string, ConnectionState> states;
        private readonly IFneTransportFactory transportFactory;
        private readonly Func<TimeSpan, Action, IDisposable> scheduler;
        private readonly object lifecycleLock = new object();

        private volatile bool disposed;

        /// <inheritdoc />
        public event Action<FneConnectionSnapshot>? StateChanged;

        /// <summary>
        /// Creates the service for the given systems. A null systems
        /// list is treated as empty; a null scheduler selects the
        /// default timer-based one-shot scheduler.
        /// </summary>
        /// <param name="systems">Configured systems, or null for none.</param>
        /// <param name="transportFactory">Transport factory; must not be null.</param>
        /// <param name="scheduler">One-shot scheduler, or null for the default.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="transportFactory"/> is null.
        /// </exception>
        public FneConnectionService(
            IReadOnlyList<Codeplug.System>? systems,
            IFneTransportFactory transportFactory,
            Func<TimeSpan, Action, IDisposable>? scheduler = null)
        {
            this.transportFactory = transportFactory
                ?? throw new ArgumentNullException(nameof(transportFactory));
            this.scheduler = scheduler ?? DefaultSchedule;

            var states = new Dictionary<string, ConnectionState>(StringComparer.OrdinalIgnoreCase);
            if (systems is not null)
            {
                foreach (var system in systems)
                {
                    if (system is null || string.IsNullOrWhiteSpace(system.Name))
                    {
                        continue;
                    }

                    states[system.Name] = new ConnectionState
                    {
                        SystemName = system.Name,
                        SystemConfig = system,
                    };
                }
            }

            this.states = states;
        }

        /// <inheritdoc />
        public void Start(string systemName)
        {
            var state = Find(systemName);
            if (state is null)
            {
                return;
            }

            lock (state.Sync)
            {
                // WPF parity: a row whose peer is already started is a
                // no-op (MainWindow.FneConnections.cs:75). Without this
                // guard a second Start on a connected row creates a new
                // transport and overwrites state.Transport, leaking the
                // live one without unsubscribing or disconnecting it.
                if (disposed || state.IsBusy || state.IsStarted)
                {
                    return;
                }

                state.IsBusy = true;
                state.IsConnected = false;
                state.IsStarted = false;
                Publish(state);

                try
                {
                    var transport = transportFactory.Create(state.SystemConfig!);
                    Subscribe(state, transport);
                    state.Transport = transport;
                    transport.Connect();
                }
                catch
                {
                    // A failed create/connect never publishes a connected
                    // snapshot: clear busy and report the disconnected
                    // final state instead.
                    CancelHeartbeat(state);
                    TeardownTransport(state);
                    state.IsBusy = false;
                    state.IsConnected = false;
                    state.IsStarted = false;
                    Publish(state);
                }
            }
        }

        /// <inheritdoc />
        public void Stop(string systemName)
        {
            var state = Find(systemName);
            if (state is null)
            {
                return;
            }

            lock (state.Sync)
            {
                if (disposed || state.IsBusy || !state.IsStarted)
                {
                    return;
                }

                state.IsBusy = true;
                Publish(state);

                CancelHeartbeat(state);
                TeardownTransport(state);

                state.IsConnected = false;
                state.IsBusy = false;
                state.IsStarted = false;
                Publish(state);
            }
        }

        /// <inheritdoc />
        public void Restart(string systemName)
        {
            var state = Find(systemName);
            if (state is null)
            {
                return;
            }

            lock (state.Sync)
            {
                if (disposed || state.IsBusy)
                {
                    return;
                }

                // Disconnect and dispose the current transport, hold
                // busy across the injected delay, then reconnect with a
                // fresh transport when the scheduler fires.
                state.IsBusy = true;
                CancelHeartbeat(state);
                TeardownTransport(state);
                state.IsConnected = false;
                state.IsStarted = false;
                Publish(state);

                state.RestartHandle = scheduler(RestartDelay, () => ReconnectAfterRestart(state));
            }
        }

        /// <inheritdoc />
        public FneConnectionSnapshot? GetSnapshot(string systemName)
        {
            var state = Find(systemName);
            if (state is null)
            {
                return null;
            }

            lock (state.Sync)
            {
                return CreateSnapshot(state);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (lifecycleLock)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
            }

            // Tear down every system quietly: disconnect and dispose
            // transports, cancel schedulers, unsubscribe events. No
            // StateChanged is published after disposal.
            foreach (var state in states.Values)
            {
                lock (state.Sync)
                {
                    CancelHeartbeat(state);
                    CancelRestart(state);
                    TeardownTransport(state);
                    state.IsConnected = false;
                    state.IsBusy = false;
                    state.IsStarted = false;
                }
            }
        }

        /// <summary>
        /// Reconnects the system after a restart's delay: creates a
        /// fresh transport and connects it. Busy stays set until the
        /// fresh transport raises PeerConnected.
        /// </summary>
        private void ReconnectAfterRestart(ConnectionState state)
        {
            lock (state.Sync)
            {
                if (disposed || state.RestartHandle is null || !state.IsBusy)
                {
                    return;
                }

                state.RestartHandle = null;

                try
                {
                    var transport = transportFactory.Create(state.SystemConfig!);
                    Subscribe(state, transport);
                    state.Transport = transport;
                    transport.Connect();
                }
                catch
                {
                    CancelHeartbeat(state);
                    TeardownTransport(state);
                    state.IsBusy = false;
                    state.IsConnected = false;
                    state.IsStarted = false;
                    Publish(state);
                }
            }
        }

        /// <summary>
        /// Schedules the next heartbeat tick for the system, replacing
        /// the previous tick's slot. Each tick runs once; the tick
        /// itself re-schedules the next one.
        /// </summary>
        private void ScheduleHeartbeat(ConnectionState state)
        {
            // The injected scheduler fires every uncancelled slot when
            // time elapses, so the previous slot must be cancelled
            // before a new one is scheduled.
            state.HeartbeatHandle?.Dispose();
            state.HeartbeatHandle = scheduler(HeartbeatPeriod, () => OnHeartbeatTick(state));
        }

        /// <summary>
        /// Runs one heartbeat tick. The acknowledgement flag belongs to
        /// the preceding interval: receive callbacks arrive on the
        /// transport's receive thread and may race this timer callback.
        /// The transport ping is deliberately invoked outside state.Sync;
        /// otherwise the receive callback cannot acquire the lock to
        /// record a valid acknowledgement before this method checks it.
        /// Two consecutive intervals without an acknowledgement time the
        /// connection out.
        /// </summary>
        private void OnHeartbeatTick(ConnectionState state)
        {
            IFneTransport? transport;
            bool timeout;

            lock (state.Sync)
            {
                if (disposed || !state.IsConnected || state.Transport is not { } currentTransport)
                {
                    return;
                }

                transport = currentTransport;
                if (state.AckObserved)
                {
                    state.MissedPings = 0;
                }
                else
                {
                    state.MissedPings++;
                }

                // Consume only the acknowledgement for the interval just
                // completed. A PONG arriving after this point belongs to
                // the ping sent below and will be observed by the next tick.
                state.AckObserved = false;
                timeout = state.MissedPings >= 2;

                if (!timeout)
                {
                    ScheduleHeartbeat(state);
                }
            }

            try
            {
                // Never call transport code while state.Sync is held. The
                // real PONG callback runs on fnecore's receive thread.
                transport.Ping();
            }
            catch
            {
                // A throwing keepalive is observed as a missing
                // acknowledgement on the next interval.
            }

            if (!timeout)
            {
                return;
            }

            lock (state.Sync)
            {
                // The current ping can acknowledge synchronously or before
                // this lock is reacquired. Preserve a live connection in
                // that case; otherwise tear down the same transport that
                // supplied the two-missed-interval result.
                if (disposed || !state.IsConnected || !ReferenceEquals(state.Transport, transport))
                {
                    return;
                }

                if (state.AckObserved)
                {
                    state.MissedPings = 0;
                    state.AckObserved = false;
                    ScheduleHeartbeat(state);
                    return;
                }

                CancelHeartbeat(state);
                TeardownTransport(state);
                state.IsConnected = false;
                state.IsBusy = false;
                state.IsStarted = false;
                Publish(state);
            }
        }

        /// <summary>
        /// Handles the transport's PeerConnected event: the row becomes
        /// connected and not busy, and heartbeat scheduling begins.
        /// </summary>
        private void OnPeerConnected(ConnectionState state, IFneTransport transport)
        {
            lock (state.Sync)
            {
                if (disposed || !ReferenceEquals(state.Transport, transport))
                {
                    return;
                }

                state.IsConnected = true;
                state.IsBusy = false;
                state.IsStarted = true;
                state.MissedPings = 0;
                Publish(state);
                ScheduleHeartbeat(state);
            }
        }

        /// <summary>
        /// Handles the transport's PeerDisconnected event: the row
        /// returns to the disconnected final state.
        /// </summary>
        private void OnPeerDisconnected(ConnectionState state, IFneTransport transport)
        {
            lock (state.Sync)
            {
                if (disposed || !ReferenceEquals(state.Transport, transport))
                {
                    return;
                }

                CancelHeartbeat(state);
                TeardownTransport(state);
                state.IsConnected = false;
                state.IsBusy = false;
                state.IsStarted = false;
                Publish(state);
            }
        }

        /// <summary>
        /// Handles the transport's PingAcknowledged event: records the
        /// acknowledgement so the current tick resets the miss counter.
        /// </summary>
        private void OnPingAcknowledged(ConnectionState state, IFneTransport transport)
        {
            lock (state.Sync)
            {
                if (disposed || !ReferenceEquals(state.Transport, transport))
                {
                    return;
                }

                state.AckObserved = true;
            }
        }

        /// <summary>
        /// Subscribes the per-transport event handlers and remembers the
        /// delegates so they can be unsubscribed later.
        /// </summary>
        private void Subscribe(ConnectionState state, IFneTransport transport)
        {
            state.PeerConnectedHandler = () => OnPeerConnected(state, transport);
            state.PeerDisconnectedHandler = () => OnPeerDisconnected(state, transport);
            state.PingAcknowledgedHandler = () => OnPingAcknowledged(state, transport);

            transport.PeerConnected += state.PeerConnectedHandler;
            transport.PeerDisconnected += state.PeerDisconnectedHandler;
            transport.PingAcknowledged += state.PingAcknowledgedHandler;
        }

        /// <summary>
        /// Unsubscribes the per-transport event handlers. Safe to call
        /// when nothing was subscribed.
        /// </summary>
        private void Unsubscribe(ConnectionState state, IFneTransport transport)
        {
            if (state.PeerConnectedHandler is { } onConnected)
            {
                transport.PeerConnected -= onConnected;
            }

            if (state.PeerDisconnectedHandler is { } onDisconnected)
            {
                transport.PeerDisconnected -= onDisconnected;
            }

            if (state.PingAcknowledgedHandler is { } onAcknowledged)
            {
                transport.PingAcknowledged -= onAcknowledged;
            }

            state.PeerConnectedHandler = null;
            state.PeerDisconnectedHandler = null;
            state.PingAcknowledgedHandler = null;
        }

        /// <summary>
        /// Unsubscribes, disconnects, and disposes the system's current
        /// transport, if any. Disconnect and dispose failures are
        /// swallowed: the transport is being discarded anyway.
        /// </summary>
        private void TeardownTransport(ConnectionState state)
        {
            if (state.Transport is not { } transport)
            {
                return;
            }

            Unsubscribe(state, transport);

            try
            {
                transport.Disconnect();
            }
            catch
            {
                // Best effort.
            }

            transport.Dispose();
            state.Transport = null;
        }

        /// <summary>
        /// Cancels the pending heartbeat tick, if any.
        /// </summary>
        private static void CancelHeartbeat(ConnectionState state)
        {
            state.HeartbeatHandle?.Dispose();
            state.HeartbeatHandle = null;
        }

        /// <summary>
        /// Cancels the pending restart reconnect, if any.
        /// </summary>
        private static void CancelRestart(ConnectionState state)
        {
            state.RestartHandle?.Dispose();
            state.RestartHandle = null;
        }

        /// <summary>
        /// Finds the state for a system name, case-insensitively.
        /// </summary>
        private ConnectionState? Find(string systemName)
        {
            if (string.IsNullOrWhiteSpace(systemName))
            {
                return null;
            }

            return states.TryGetValue(systemName, out var state) ? state : null;
        }

        /// <summary>
        /// Publishes the system's current state. Callers hold the
        /// system's lock.
        /// </summary>
        private void Publish(ConnectionState state)
            => StateChanged?.Invoke(CreateSnapshot(state));

        private static FneConnectionSnapshot CreateSnapshot(ConnectionState state)
            => new FneConnectionSnapshot(
                state.SystemName,
                state.IsConnected,
                state.IsBusy,
                state.IsStarted);

        /// <summary>
        /// Default one-shot scheduler: a real
        /// <see cref="System.Threading.Timer"/> that fires the action
        /// once after the delay.
        /// </summary>
        private static IDisposable DefaultSchedule(TimeSpan delay, Action action)
        {
            var timer = new Timer(
                _ => action(),
                null,
                delay,
                Timeout.InfiniteTimeSpan);

            return new TimerCancellation(timer);
        }
    }
}
