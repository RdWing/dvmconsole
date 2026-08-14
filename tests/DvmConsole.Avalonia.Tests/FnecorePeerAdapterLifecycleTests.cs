// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using fnecore;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class FnecorePeerAdapterLifecycleTests
    {
        [Fact]
        public void ConnectFailureStopsStartupAndReportsDisconnected()
        {
            var diagnostics = new List<string>();
            var adapter = new FnecorePeerAdapter(
                MakeSystem(),
                background: action => action(),
                logLevel: LogLevel.FATAL,
                rawPacketTrace: false,
                trafficLogging: false,
                startPeer: peer =>
                {
                    peer.StartWithoutMaintainence();
                    throw new InvalidOperationException("synthetic startup failure");
                });
            adapter.SetDiagnosticWriter((_, message) => diagnostics.Add(message));

            int disconnected = 0;
            adapter.PeerDisconnected += () => disconnected++;

            adapter.Connect();

            Assert.Equal(1, disconnected);
            Assert.Contains(diagnostics, message => message.Contains("FNE connect start failed", StringComparison.Ordinal));
            adapter.Dispose();
        }

        private static Codeplug.System MakeSystem()
            => new()
            {
                Name = "Adapter Lifecycle Test",
                Identity = "Console 1",
                Address = "127.0.0.1",
                Port = 62031,
                PeerId = 1000001,
                Password = "pw",
                Encrypted = false,
            };
    }
}
