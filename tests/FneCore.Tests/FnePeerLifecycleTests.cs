// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using fnecore;
using Xunit;

namespace FneCore.Tests
{
    public sealed class FnePeerLifecycleTests
    {
        [Fact]
        public async Task StopJoinsReceiveLoopsAfterMalformedTraffic()
        {
            using var master = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var peer = new FnePeer(
                "test",
                1000001,
                (IPEndPoint)master.Client.LocalEndPoint!);
            peer.LogLevel = LogLevel.FATAL;

            var malformed = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            peer.Logger = (_, message) =>
            {
                if (message.Contains("Malformed", StringComparison.Ordinal))
                    malformed.TrySetResult(true);
            };

            peer.StartWithoutMaintainence();
            peer.SendMasterTraffic(
                FneBase.CreateOpcode(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_DMR),
                new byte[] { 1 },
                0,
                1);

            UdpReceiveResult outgoing = await master.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await master.SendAsync(new byte[] { 0 }, 1, outgoing.RemoteEndPoint);
            await malformed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Task trafficTask = GetTask(peer, "listenTrafficTask");
            Task metadataTask = GetTask(peer, "listenMetadataTask");
            Assert.False(trafficTask.IsCompleted);
            Assert.False(metadataTask.IsCompleted);

            await Task.Run(peer.Stop).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(trafficTask.IsCompleted);
            Assert.True(metadataTask.IsCompleted);
            Assert.False(peer.IsStarted);
        }

        private static Task GetTask(FnePeer peer, string fieldName)
            => (Task)(typeof(FnePeer)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(peer)!);
    }
}
