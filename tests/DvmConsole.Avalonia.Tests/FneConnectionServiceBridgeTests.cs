// SPDX-License-Identifier: AGPL-3.0-only
/**
* RED contract gate for the Avalonia FNE connection-service bridge slice:
*
*   DvmConsole.Avalonia.Services.FneConnectionServiceBridge
*
* The bridge is the "one platform-neutral event adapter" (plan Task 9
* step 3): it forwards the FneConnectionManagerViewModel's
* StartRequested/StopRequested/RestartRequested events into an injected
* IFneConnectionService and marshals every service StateChanged snapshot
* back into the manager via an injected UI-post delegate (default
* Dispatcher.UIThread.Post; tests inject immediate invocation). Attach()
* wires both directions; Detach() (and IDisposable.Dispose) unwires them
* so no event can reach the manager or service afterwards.
*/
using System;
using System.Reflection;
using System.Collections.Generic;
using Avalonia.Threading;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Core.Networking;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="FneConnectionServiceBridge"/>.
    /// </summary>
    public sealed class FneConnectionServiceBridgeTests
    {
        private sealed class FakeFneConnectionService : IFneConnectionService
        {
            public readonly List<string> Started = new();
            public readonly List<string> Stopped = new();
            public readonly List<string> Restarted = new();

            public event Action<FneConnectionSnapshot>? StateChanged;

            public void Start(string systemName) => Started.Add(systemName);

            public void Stop(string systemName) => Stopped.Add(systemName);

            public void Restart(string systemName) => Restarted.Add(systemName);

            public FneConnectionSnapshot? GetSnapshot(string systemName) => null;

            public void RaiseStateChanged(FneConnectionSnapshot snapshot)
                => StateChanged?.Invoke(snapshot);

            public void Dispose()
            {
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

        private static FneConnectionManagerViewModel Manager()
            => new FneConnectionManagerViewModel(new[] { System() });

        [Fact]
        public void Bridge_MarshalViaUiPost_ApplyStateLandsOnRow()
        {
            var service = new FakeFneConnectionService();
            var manager = Manager();
            var bridge = new FneConnectionServiceBridge(service, manager, post => post());
            bridge.Attach();

            manager.StartSystem("Alpha");
            Assert.Equal(new[] { "Alpha" }, service.Started);

            service.RaiseStateChanged(new FneConnectionSnapshot("Alpha", true, false, true));

            var row = manager.Systems[0];
            Assert.True(row.IsConnected);
            Assert.True(row.IsStarted);
            Assert.False(row.IsBusy);
            Assert.True(manager.AnyConnected);

            bridge.Dispose();
        }

        [Fact]
        public void Bridge_ForwardsStopAndRestart()
        {
            var service = new FakeFneConnectionService();
            var manager = new FneConnectionManagerViewModel(new[] { System("Alpha"), System("Beta") });
            var bridge = new FneConnectionServiceBridge(service, manager, post => post());
            bridge.Attach();

            // Stop and Restart each operate on a distinct row: the manager's
            // busy guard makes a second request on the same row a no-op until
            // ApplyState clears the row, and the fake service never raises
            // StateChanged on its own.
            manager.StopSystem("Alpha");
            manager.RestartSystem("Beta");

            Assert.Equal(new[] { "Alpha" }, service.Stopped);
            Assert.Equal(new[] { "Beta" }, service.Restarted);

            bridge.Detach();
        }

        [Fact]
        public void Bridge_Detach_StopsForwarding()
        {
            var service = new FakeFneConnectionService();
            var manager = Manager();
            var bridge = new FneConnectionServiceBridge(service, manager, post => post());
            bridge.Attach();

            bridge.Detach();

            manager.StartSystem("Alpha");
            Assert.Empty(service.Started);

            service.RaiseStateChanged(new FneConnectionSnapshot("Alpha", true, false, true));
            Assert.False(manager.Systems[0].IsConnected);
        }

        [Fact]
        public void Bridge_Dispose_DetachesAndIsIdempotent()
        {
            var service = new FakeFneConnectionService();
            var manager = Manager();
            var bridge = new FneConnectionServiceBridge(service, manager, post => post());
            bridge.Attach();

            bridge.Dispose();
            bridge.Dispose(); // idempotent

            manager.StartSystem("Alpha");
            Assert.Empty(service.Started);
        }

        [Fact]
        public void Bridge_DefaultUiPost_IsDispatcherUiThreadPost()
        {
            var ctor = typeof(FneConnectionServiceBridge).GetConstructors()[0];
            var parameters = ctor.GetParameters();
            Assert.Equal(3, parameters.Length);
            Assert.Equal(typeof(IFneConnectionService), parameters[0].ParameterType);
            Assert.Equal(typeof(FneConnectionManagerViewModel), parameters[1].ParameterType);
            Assert.Equal(typeof(Action<Action>), parameters[2].ParameterType);
            Assert.True(parameters[2].HasDefaultValue);
            Assert.Null(parameters[2].DefaultValue);

            // The bridge resolves its default post delegate from the
            // Avalonia UI dispatcher, exactly like App.axaml.cs and
            // MainWindow.axaml.cs do for other cross-thread callbacks.
            var defaultPost = typeof(FneConnectionServiceBridge).GetField(
                "DefaultUiPost",
                BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(defaultPost);
            Assert.Equal(typeof(Action<Action>), defaultPost!.FieldType);
        }

        [Fact]
        public void Bridge_NullArguments_Throw()
        {
            var manager = Manager();
            Assert.Throws<ArgumentNullException>(() =>
                new FneConnectionServiceBridge(null!, manager, post => post()));
            Assert.Throws<ArgumentNullException>(() =>
                new FneConnectionServiceBridge(new FakeFneConnectionService(), null!, post => post()));
        }

        [Fact]
        public void Bridge_AttachAfterDetach_ReattachesCleanly()
        {
            var service = new FakeFneConnectionService();
            var manager = Manager();
            var bridge = new FneConnectionServiceBridge(service, manager, post => post());

            bridge.Attach();
            bridge.Detach();
            bridge.Attach();

            manager.StartSystem("Alpha");
            Assert.Equal(new[] { "Alpha" }, service.Started);

            service.RaiseStateChanged(new FneConnectionSnapshot("Alpha", true, false, true));
            Assert.True(manager.Systems[0].IsConnected);

            bridge.Detach();
        }

        [Fact]
        public void Bridge_IsSealedAndDisposable()
        {
            var type = typeof(FneConnectionServiceBridge);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetMethod("Attach"));
            Assert.NotNull(type.GetMethod("Detach"));
            Assert.True(typeof(IDisposable).IsAssignableFrom(type));
        }
    }
}
