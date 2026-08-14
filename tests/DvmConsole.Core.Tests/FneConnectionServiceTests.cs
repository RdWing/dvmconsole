// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the headless FNE connection service slice
* (plan Task 4 / Task 9 step 3 "one platform-neutral event adapter").
*
* Production surface locked here (DvmConsole.Core/Networking):
*   FneConnectionSnapshot        sealed positional record
*                                (SystemName, IsConnected, IsBusy, IsStarted)
*   IFneTransport                Connect/Disconnect/Ping + PeerConnected/
*                                PeerDisconnected/PingAcknowledged + IDisposable
*   IFneTransportFactory         Create(Codeplug.System) -> IFneTransport
*   IFneConnectionService        Start/Stop/Restart(string), GetSnapshot(string),
*                                StateChanged event, IDisposable
*   FneConnectionService         headless implementation (no fnecore, no
*                                sockets, no audio, no dispatcher, no secrets)
*
* Lifecycle locked here:
*   Start(name): unknown name -> no-op; busy -> no-op. Else busy=true,
*     StateChanged(busy), create transport via factory, Connect().
*     Transport PeerConnected -> StateChanged(connected, busy=false,
*     started=true) and heartbeat scheduling begins.
*   Stop(name): unknown/busy/not-started -> no-op. Else busy=true,
*     StateChanged(busy), Disconnect(), final StateChanged(disconnected,
*     started=false, busy=false); transport disposed.
*   Restart(name): busy guard; disconnect + busy held across the injected
*     scheduler delay, then reconnect (fresh transport); PeerConnected
*     clears busy. WPF parity: Task.Delay(250) (MainWindow.FneConnections.cs:155).
*   Heartbeat: while connected, periodic tick via injected scheduler
*     (fnecore parity PingTime=5); each tick Pings the transport; two
*     consecutive ticks without PingAcknowledged -> disconnected.
*   Dispose: stops everything, unsubscribes, disposes transports, no
*     further StateChanged.
*
* Seam decision (parent): IFneTransport mirrors fnecore.FnePeer's actual
* observable events (PeerConnected on login handshake, PeerDisconnected on
* connection-state loss) plus the maintainence ping loop (FnePeer.cs:1582)
* as Ping()/PingAcknowledged, so the headless service can implement the
* keepalive deterministically behind the injected scheduler.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Core.Networking;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for the headless FNE connection service.
    /// </summary>
    public sealed class FneConnectionServiceTests
    {
        /* ------------------------------------------------------------------
        ** Test doubles
        ** ---------------------------------------------------------------- */

        private sealed class FakeFneTransport : IFneTransport
        {
            public bool ThrowOnConnect;
            public bool AckOnPing;
            public Func<int, Task>? AckOnPingAsync;
            public Action<int>? AcknowledgementObserved;
            public int ConnectCount;
            public int DisconnectCount;
            public int PingCount;
            public int DisposeCount;

            public event Action? PeerConnected;
            public event Action? PeerDisconnected;
            public event Action? PingAcknowledged;

            public void Connect()
            {
                ConnectCount++;
                if (ThrowOnConnect)
                {
                    throw new InvalidOperationException("simulated connect failure");
                }
            }

            public void Disconnect() => DisconnectCount++;

            public void Ping()
            {
                PingCount++;
                if (AckOnPingAsync is { } acknowledgeAsync)
                {
                    var pingNumber = PingCount;
                    _ = Task.Run(async () =>
                    {
                        await acknowledgeAsync(pingNumber).ConfigureAwait(false);
                        try
                        {
                            PingAcknowledged?.Invoke();
                        }
                        finally
                        {
                            AcknowledgementObserved?.Invoke(pingNumber);
                        }
                    });
                }
                else if (AckOnPing)
                {
                    PingAcknowledged?.Invoke();
                }
            }

            public void RaiseConnected() => PeerConnected?.Invoke();
            public void RaiseDisconnected() => PeerDisconnected?.Invoke();
            public void RaisePingAcknowledged() => PingAcknowledged?.Invoke();

            public void Dispose() => DisposeCount++;
        }

        private sealed class RecordingTransportFactory : IFneTransportFactory
        {
            private readonly Dictionary<string, FakeFneTransport> transports = new();

            public int CreateCount { get; private set; }

            /// <summary>Configures every transport this factory creates.</summary>
            public Func<FakeFneTransport>? CreateHook;

            public FakeFneTransport this[string systemName] => transports[systemName];

            public IFneTransport Create(Codeplug.System system)
            {
                CreateCount++;
                var transport = CreateHook?.Invoke() ?? new FakeFneTransport();
                transports[system.Name] = transport;
                return transport;
            }
        }

        /// <summary>
        /// One-shot scheduler double: stores scheduled actions and fires
        /// them only when the test elapses time. Cancel prevents a fire.
        /// </summary>
        private sealed class ManualScheduler
        {
            private sealed class Scheduled
            {
                public Action? Action;
            }

            private readonly List<Scheduled> scheduled = new();

            public IDisposable Schedule(TimeSpan delay, Action action)
            {
                var entry = new Scheduled { Action = action };
                scheduled.Add(entry);
                return new Cancellation(() => entry.Action = null);
            }

            /// <summary>Fires every currently scheduled, uncancelled action once.</summary>
            public void Elapse()
            {
                foreach (var entry in scheduled.ToList())
                {
                    entry.Action?.Invoke();
                }
            }

            private sealed class Cancellation : IDisposable
            {
                private readonly Action cancel;

                public Cancellation(Action cancel) => this.cancel = cancel;

                public void Dispose() => cancel();
            }
        }

        private static Codeplug.System System(string name = "Alpha")
            => new Codeplug.System
            {
                Name = name,
                Identity = "CONSOLE-1",
                Address = "127.0.0.1",
                Port = 56000,
                PeerId = 1000,
                Encrypted = false,
            };

        private static FneConnectionService CreateService(
            IReadOnlyList<Codeplug.System>? systems,
            RecordingTransportFactory factory,
            ManualScheduler scheduler)
            => new FneConnectionService(systems, factory, scheduler.Schedule);

        /* ------------------------------------------------------------------
        ** Start
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Start_UnknownSystem_NoOp()
        {
            var factory = new RecordingTransportFactory();
            var service = CreateService(new[] { System() }, factory, new ManualScheduler());
            var changes = new List<FneConnectionSnapshot>();
            service.StateChanged += changes.Add;

            service.Start("Nope");

            Assert.Equal(0, factory.CreateCount);
            Assert.Empty(changes);
            Assert.Null(service.GetSnapshot("Nope"));
        }

        [Fact]
        public void Start_BusyGuard_NoDoubleStart()
        {
            var factory = new RecordingTransportFactory();
            var service = CreateService(new[] { System() }, factory, new ManualScheduler());

            service.Start("Alpha");
            service.Start("Alpha");

            Assert.Equal(1, factory.CreateCount);
            Assert.True(service.GetSnapshot("Alpha")!.IsBusy);
        }

        [Fact]
        public void Start_ConnectingThenConnected_StateChangedOrder_BusyClears()
        {
            var factory = new RecordingTransportFactory();
            var scheduler = new ManualScheduler();
            var service = CreateService(new[] { System() }, factory, scheduler);
            var changes = new List<FneConnectionSnapshot>();
            service.StateChanged += changes.Add;

            service.Start("Alpha");
            var busySnapshot = service.GetSnapshot("Alpha")!;
            Assert.True(busySnapshot.IsBusy);
            Assert.False(busySnapshot.IsConnected);
            Assert.False(busySnapshot.IsStarted);

            factory["Alpha"].RaiseConnected();

            Assert.Equal(2, changes.Count);
            Assert.True(changes[0].IsBusy);
            Assert.False(changes[0].IsConnected);
            Assert.Equal(new FneConnectionSnapshot("Alpha", true, false, true), changes[1]);
            var final = service.GetSnapshot("Alpha")!;
            Assert.True(final.IsConnected);
            Assert.True(final.IsStarted);
            Assert.False(final.IsBusy);
        }

        [Fact]
        public void Start_AlreadyStarted_NoOp()
        {
            // WPF parity: StartFneSystemAsync returns early when the
            // entry's peer IsStarted (MainWindow.FneConnections.cs:75).
            // A second Start on a connected row must not create a second
            // transport or disturb the live connection.
            var factory = new RecordingTransportFactory();
            var service = CreateService(new[] { System() }, factory, new ManualScheduler());
            service.Start("Alpha");
            factory["Alpha"].RaiseConnected();
            Assert.Equal(1, factory.CreateCount);
            var connected = service.GetSnapshot("Alpha")!;
            Assert.True(connected.IsConnected);

            service.Start("Alpha");

            Assert.Equal(1, factory.CreateCount);
            Assert.Equal(0, factory["Alpha"].DisconnectCount);
            Assert.Equal(connected, service.GetSnapshot("Alpha"));
        }

        [Fact]
        public void Service_DefaultScheduler_ConstructsStartsAndDisposes()
        {
            // Exercises the production default Timer-based one-shot
            // scheduler without depending on real timing: construct,
            // start, connect, dispose. A scheduler leak would surface as
            // a hung timer or a dispose failure here.
            var factory = new RecordingTransportFactory();
            using (var service = new FneConnectionService(new[] { System() }, factory))
            {
                service.Start("Alpha");
                factory["Alpha"].RaiseConnected();
                Assert.True(service.GetSnapshot("Alpha")!.IsConnected);
            }

            Assert.Equal(1, factory["Alpha"].DisposeCount);
        }

        /* ------------------------------------------------------------------
        ** Stop
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Stop_Connected_ClearsConnectedAndStarted()
        {
            var factory = new RecordingTransportFactory();
            var service = CreateService(new[] { System() }, factory, new ManualScheduler());

            service.Start("Alpha");
            factory["Alpha"].RaiseConnected();
            var changes = new List<FneConnectionSnapshot>();
            service.StateChanged += changes.Add;

            service.Stop("Alpha");

            var final = service.GetSnapshot("Alpha")!;
            Assert.False(final.IsConnected);
            Assert.False(final.IsStarted);
            Assert.False(final.IsBusy);
            Assert.True(changes[0].IsBusy);
            Assert.Equal(new FneConnectionSnapshot("Alpha", false, false, false), changes[^1]);
            Assert.Equal(1, factory["Alpha"].DisconnectCount);
            Assert.Equal(1, factory["Alpha"].DisposeCount);
        }

        [Fact]
        public void Stop_UnknownOrBusy_NoOp()
        {
            var factory = new RecordingTransportFactory();
            var service = CreateService(new[] { System() }, factory, new ManualScheduler());
            var changes = new List<FneConnectionSnapshot>();
            service.StateChanged += changes.Add;

            service.Stop("Nope");
            Assert.Empty(changes);

            service.Start("Alpha");
            service.Stop("Alpha"); // busy (not yet connected) -> no-op
            Assert.Single(changes);
            Assert.Equal(0, factory["Alpha"].DisconnectCount);
            Assert.True(service.GetSnapshot("Alpha")!.IsBusy);
        }

        /* ------------------------------------------------------------------
        ** Restart
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Restart_DisconnectsThenConnects_BusyHeldAcrossDelay()
        {
            var factory = new RecordingTransportFactory();
            var scheduler = new ManualScheduler();
            var service = CreateService(new[] { System() }, factory, scheduler);

            service.Start("Alpha");
            factory["Alpha"].RaiseConnected();
            Assert.Equal(1, factory.CreateCount);

            service.Restart("Alpha");

            // Busy is held across the injected delay; old transport is gone.
            var during = service.GetSnapshot("Alpha")!;
            Assert.True(during.IsBusy);
            Assert.False(during.IsConnected);
            Assert.Equal(1, factory["Alpha"].DisconnectCount);

            scheduler.Elapse(); // reconnect fires
            Assert.Equal(2, factory.CreateCount);
            Assert.True(service.GetSnapshot("Alpha")!.IsBusy);

            factory["Alpha"].RaiseConnected();
            var final = service.GetSnapshot("Alpha")!;
            Assert.True(final.IsConnected);
            Assert.False(final.IsBusy);
            Assert.True(final.IsStarted);
        }

        /* ------------------------------------------------------------------
        ** Heartbeat
        ** ---------------------------------------------------------------- */

        [Fact]
        public void HeartbeatTimeout_ConnectedToDisconnected()
        {
            var factory = new RecordingTransportFactory();
            var scheduler = new ManualScheduler();
            var service = CreateService(new[] { System() }, factory, scheduler);

            service.Start("Alpha");
            factory["Alpha"].RaiseConnected();
            Assert.True(service.GetSnapshot("Alpha")!.IsConnected);

            scheduler.Elapse(); // tick 1: ping, no ack -> 1 miss, still connected
            Assert.True(service.GetSnapshot("Alpha")!.IsConnected);
            Assert.Equal(1, factory["Alpha"].PingCount);

            scheduler.Elapse(); // tick 2: no ack -> 2 consecutive misses -> timeout
            var final = service.GetSnapshot("Alpha")!;
            Assert.False(final.IsConnected);
            Assert.False(final.IsStarted);
            Assert.False(final.IsBusy);
            Assert.Equal(1, factory["Alpha"].DisconnectCount);
            Assert.Equal(2, factory["Alpha"].PingCount);
        }

        [Fact]
        public void PingAck_ResetsTimeout_StaysConnected()
        {
            var factory = new RecordingTransportFactory();
            var scheduler = new ManualScheduler();
            var service = CreateService(new[] { System() }, factory, scheduler);
            factory.CreateHook = () => new FakeFneTransport { AckOnPing = true };

            service.Start("Alpha");
            factory["Alpha"].RaiseConnected();

            for (var i = 0; i < 5; i++)
            {
                scheduler.Elapse();
            }

            Assert.True(service.GetSnapshot("Alpha")!.IsConnected);
            Assert.Equal(5, factory["Alpha"].PingCount);
            Assert.Equal(0, factory["Alpha"].DisconnectCount);
        }

        [Fact]
        public async Task Heartbeat_CrossThreadAcknowledgements_DoNotTimeoutConnectedPeer()
        {
            var factory = new RecordingTransportFactory();
            var scheduler = new ManualScheduler();
            var firstPingStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondPingStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstAcknowledgement = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondAcknowledgement = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstAckRaised = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondAckRaised = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            factory.CreateHook = () => new FakeFneTransport
            {
                AckOnPingAsync = async pingNumber =>
                {
                    var started = pingNumber == 1 ? firstPingStarted : secondPingStarted;
                    var acknowledgement = pingNumber == 1
                        ? firstAcknowledgement
                        : secondAcknowledgement;
                    started.TrySetResult(true);
                    await acknowledgement.Task.ConfigureAwait(false);
                },
                AcknowledgementObserved = pingNumber =>
                {
                    if (pingNumber == 1)
                    {
                        firstAckRaised.TrySetResult(true);
                    }
                    else
                    {
                        secondAckRaised.TrySetResult(true);
                    }
                },
            };

            var service = CreateService(new[] { System() }, factory, scheduler);
            service.Start("Alpha");
            factory["Alpha"].RaiseConnected();

            var firstTick = Task.Run(scheduler.Elapse);
            await firstPingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await firstTick.WaitAsync(TimeSpan.FromSeconds(5));
            firstAcknowledgement.TrySetResult(true);
            await firstAckRaised.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var secondTick = Task.Run(scheduler.Elapse);
            await secondPingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await secondTick.WaitAsync(TimeSpan.FromSeconds(5));
            secondAcknowledgement.TrySetResult(true);
            await secondAckRaised.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(service.GetSnapshot("Alpha")!.IsConnected);
            Assert.Equal(2, factory["Alpha"].PingCount);
            Assert.Equal(0, factory["Alpha"].DisconnectCount);

            service.Dispose();
        }

        /* ------------------------------------------------------------------
        ** Threading / snapshot / disposal / concurrency
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task StateChanged_RaisedFromTransportThread_NoDispatcherInvolved()
        {
            var factory = new RecordingTransportFactory();
            var service = CreateService(new[] { System() }, factory, new ManualScheduler());
            var received = new TaskCompletionSource<FneConnectionSnapshot>();
            service.StateChanged += snap =>
            {
                if (snap.IsConnected)
                {
                    received.TrySetResult(snap);
                }
            };

            service.Start("Alpha");
            await Task.Run(() => factory["Alpha"].RaiseConnected());

            var snapshot = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Alpha", snapshot.SystemName);
            Assert.True(snapshot.IsConnected);
        }

        [Fact]
        public void Snapshot_IsStartedIsConnected_MatchState()
        {
            var factory = new RecordingTransportFactory();
            var service = CreateService(new[] { System() }, factory, new ManualScheduler());

            Assert.Null(service.GetSnapshot("Nope"));

            service.Start("Alpha");
            Assert.True(service.GetSnapshot("Alpha")!.IsBusy);
            Assert.False(service.GetSnapshot("Alpha")!.IsStarted);

            factory["Alpha"].RaiseConnected();
            var connected = service.GetSnapshot("Alpha")!;
            Assert.True(connected.IsConnected);
            Assert.True(connected.IsStarted);
            Assert.False(connected.IsBusy);

            service.Stop("Alpha");
            var stopped = service.GetSnapshot("Alpha")!;
            Assert.False(stopped.IsConnected);
            Assert.False(stopped.IsStarted);
            Assert.False(stopped.IsBusy);
        }

        [Fact]
        public void Dispose_StopsAllAndUnsubscribes()
        {
            var factory = new RecordingTransportFactory();
            var service = CreateService(new[] { System() }, factory, new ManualScheduler());
            service.Start("Alpha");
            factory["Alpha"].RaiseConnected();

            var changes = new List<FneConnectionSnapshot>();
            service.StateChanged += changes.Add;

            service.Dispose();

            factory["Alpha"].RaiseConnected();
            factory["Alpha"].RaiseDisconnected();
            factory["Alpha"].RaisePingAcknowledged();
            Assert.Empty(changes);
            Assert.Equal(1, factory["Alpha"].DisposeCount);
            Assert.Equal(1, factory["Alpha"].DisconnectCount);
        }

        [Fact]
        public async Task ConcurrentStartStop_SameSystem_Serialized()
        {
            var factory = new RecordingTransportFactory();
            var service = CreateService(new[] { System() }, factory, new ManualScheduler());

            var start = Task.Run(() => service.Start("Alpha"));
            var stop = Task.Run(() => service.Stop("Alpha"));
            await Task.WhenAll(start, stop);

            Assert.Equal(1, factory.CreateCount);
            var snapshot = service.GetSnapshot("Alpha")!;
            // Stop is a no-op while the row is busy or not started, so the
            // concurrent stop cannot tear down the in-flight start; the
            // final state is deterministic: busy until PeerConnected, never
            // connected, exactly one transport ever created.
            Assert.True(snapshot.IsBusy);
            Assert.False(snapshot.IsConnected);
            Assert.False(snapshot.IsStarted);
            Assert.Equal(0, factory["Alpha"].DisconnectCount);
        }

        [Fact]
        public void TransportConnectFailure_ClearsBusy_RaisesDisconnected()
        {
            var factory = new RecordingTransportFactory
            {
                CreateHook = () => new FakeFneTransport { ThrowOnConnect = true },
            };
            var service = CreateService(new[] { System() }, factory, new ManualScheduler());
            var changes = new List<FneConnectionSnapshot>();
            service.StateChanged += changes.Add;

            service.Start("Alpha");

            var snapshot = service.GetSnapshot("Alpha")!;
            Assert.False(snapshot.IsBusy);
            Assert.False(snapshot.IsConnected);
            Assert.False(snapshot.IsStarted);
            Assert.Equal(new FneConnectionSnapshot("Alpha", false, false, false), changes[^1]);
            Assert.True(changes[0].IsBusy);
            Assert.DoesNotContain(changes, s => s.IsConnected);
        }

        /* ------------------------------------------------------------------
        ** Shape and assembly gates
        ** ---------------------------------------------------------------- */

        [Fact]
        public void FneConnectionSnapshot_IsSealedPositionalRecord()
        {
            var type = typeof(FneConnectionSnapshot);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[]
            {
                typeof(string), typeof(bool), typeof(bool), typeof(bool),
            }));
            Assert.Equal(
                new[] { "IsBusy", "IsConnected", "IsStarted", "SystemName" },
                type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => p.Name)
                    .OrderBy(n => n));
        }

        [Fact]
        public void IfneConnectionService_SurfaceIsMinimalAndSecretFree()
        {
            var type = typeof(IFneConnectionService);
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName)
                .Select(m => m.Name)
                .OrderBy(n => n)
                .ToArray();
            Assert.Equal(new[] { "Dispose", "GetSnapshot", "Restart", "Start", "Stop" }, methods);
            Assert.NotNull(type.GetEvent("StateChanged"));
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Contains("Rid", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void IfneTransport_SurfaceIsMinimal()
        {
            var type = typeof(IFneTransport);
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName)
                .Select(m => m.Name)
                .OrderBy(n => n)
                .ToArray();
            Assert.Equal(new[] { "Connect", "Disconnect", "Dispose", "Ping" }, methods);
            Assert.NotNull(type.GetEvent("PeerConnected"));
            Assert.NotNull(type.GetEvent("PeerDisconnected"));
            Assert.NotNull(type.GetEvent("PingAcknowledged"));
        }

        [Fact]
        public void IfneTransportFactory_SurfaceIsMinimal()
        {
            var methods = typeof(IFneTransportFactory)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName)
                .ToArray();
            var create = Assert.Single(methods);
            Assert.Equal("Create", create.Name);
            Assert.Equal(typeof(Codeplug.System), create.GetParameters()[0].ParameterType);
            Assert.Equal(typeof(IFneTransport), create.ReturnType);
        }

        [Fact]
        public void CoreNetworkingAssembly_HasNoFnecoreOrUiReferences()
        {
            var references = typeof(FneConnectionService).Assembly
                .GetReferencedAssemblies()
                .Select(a => a.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.DoesNotContain("fnecore", references);
            Assert.DoesNotContain("Avalonia", references);
            Assert.DoesNotContain("System.Windows", references);
            Assert.DoesNotContain("PresentationFramework", references);
        }

        [Fact]
        public void FneConnectionService_NullFactory_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new FneConnectionService(new[] { System() }, null!));
        }
    }
}
