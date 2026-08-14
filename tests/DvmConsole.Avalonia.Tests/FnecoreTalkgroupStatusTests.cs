// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using DvmConsole.Core.Networking;
using fnecore;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class FnecoreTalkgroupStatusTests
    {
        [Fact]
        public void AdapterExposesFailClosedAvailabilityBeforeAnnouncements()
        {
            var adapter = new FnecorePeerAdapter(
                MakeSystem(),
                background: action => action(),
                logLevel: LogLevel.FATAL,
                rawPacketTrace: false,
                trafficLogging: false,
                startPeer: _ => { });

            var provider = Assert.IsAssignableFrom<IFneTalkgroupStatusProvider>(adapter);
            var result = provider.QueryTalkgroupAvailability(
                new TalkgroupQuery(31001, slot: 1, TalkgroupMode.Dmr));

            Assert.False(result.IsAvailable);
            Assert.False(result.IsKnown);
            adapter.Dispose();
        }

        private static Codeplug.System MakeSystem()
            => new()
            {
                Name = "Talkgroup Status Test",
                Identity = "Console 1",
                Address = "127.0.0.1",
                Port = 62031,
                PeerId = 1000001,
                Password = "pw",
                Encrypted = false,
            };
    }
}
