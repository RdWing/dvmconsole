// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using fnecore;
using Xunit;

namespace FneCore.Tests
{
    public sealed class TrafficLoggingTests
    {
        [Fact]
        public void FnePeer_TrafficLogging_IsOptInAndMutable()
        {
            var peer = new FnePeer(
                "test",
                1000001,
                new IPEndPoint(IPAddress.Loopback, 62031));

            Assert.False(peer.TrafficLogging);

            peer.TrafficLogging = true;

            Assert.True(peer.TrafficLogging);
        }

        [Fact]
        public async Task FnePeer_TrafficLoggingEmitsDecodedDmrSummaryInRelease()
        {
            using var master = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var peer = new FnePeer(
                "test",
                1000001,
                (IPEndPoint)master.Client.LocalEndPoint);
            var logged = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            peer.LogLevel = LogLevel.FATAL;
            peer.TrafficLogging = true;
            peer.Logger = (level, message) =>
            {
                if (level == LogLevel.DEBUG && message.Contains("DMRD", StringComparison.Ordinal))
                    logged.TrySetResult(message);
            };

            try
            {
                peer.StartWithoutMaintainence();
                peer.SendMasterTraffic(
                    FneBase.CreateOpcode(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_DMR),
                    new byte[1],
                    0,
                    1);

                UdpReceiveResult outgoing = await master.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
                byte[] payload = new byte[16];
                payload[7] = 0x01;
                payload[10] = 0x02;
                payload[15] = 0x00;
                byte[] frame = new TrafficFrameProbe().Write(payload);
                await master.SendAsync(frame, frame.Length, outgoing.RemoteEndPoint);

                string message = await logged.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Contains("SRC_ID 1", message);
                Assert.Contains("DST_ID 2", message);
            }
            finally
            {
                if (peer.IsStarted)
                    peer.Stop();
            }
        }

        private sealed class TrafficFrameProbe : FneBase
        {
            public TrafficFrameProbe()
                : base("TrafficFrameProbe", 1)
            {
            }

            public byte[] Write(byte[] message)
                => WriteFrame(
                    message,
                    1,
                    1,
                    FneBase.CreateOpcode(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_DMR),
                    0,
                    1);

            public override void Start()
            {
            }

            public override void Stop()
            {
            }
        }
    }
}