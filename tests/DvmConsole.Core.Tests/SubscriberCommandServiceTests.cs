// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Core.Networking;
using Xunit;

namespace DvmConsole.Core.Tests
{
    public sealed class SubscriberCommandServiceTests
    {
        [Fact]
        public async Task UnsupportedDmrCommandDoesNotTouchTransport()
        {
            var transport = new RecordingSubscriberTransport();
            var service = CreateService(transport);

            var result = await service.ExecuteAsync(Request(SubscriberCommandMode.Dmr));

            Assert.Equal(SubscriberCommandStatus.UnsupportedMode, result.Status);
            Assert.Equal(0, transport.SendCount);
        }

        [Fact]
        public async Task InvalidDestinationIsRejectedBeforeConnectionLookup()
        {
            var transport = new RecordingSubscriberTransport();
            var service = new SubscriberCommandService(
                _ => throw new InvalidOperationException("connection lookup should not run"),
                _ => transport,
                TimeSpan.FromSeconds(1));

            var result = await service.ExecuteAsync(Request(destinationId: "not-a-rid"));

            Assert.Equal(SubscriberCommandStatus.InvalidRequest, result.Status);
            Assert.Equal(0, transport.SendCount);
        }

        [Fact]
        public async Task DisconnectedSystemBlocksWithoutSending()
        {
            var transport = new RecordingSubscriberTransport();
            var service = new SubscriberCommandService(_ => false, _ => transport, TimeSpan.FromSeconds(1));

            var result = await service.ExecuteAsync(Request());

            Assert.Equal(SubscriberCommandStatus.Disconnected, result.Status);
            Assert.Equal(0, transport.SendCount);
        }

        [Fact]
        public async Task SuccessfulCommandIsSentExactlyOnce()
        {
            var transport = new RecordingSubscriberTransport();
            var service = CreateService(transport);

            var result = await service.ExecuteAsync(Request());

            Assert.Equal(SubscriberCommandStatus.Succeeded, result.Status);
            Assert.Equal(1, transport.SendCount);
            Assert.Equal(SubscriberCommandKind.Page, transport.LastRequest.Kind);
        }

        [Fact]
        public async Task SameOwnerCannotOverlapCommands()
        {
            var transport = new RecordingSubscriberTransport
            {
                Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var service = CreateService(transport);

            Task<SubscriberCommandResult> first = service.ExecuteAsync(Request());
            await transport.Started.Task;
            var second = await service.ExecuteAsync(Request());

            Assert.Equal(SubscriberCommandStatus.Busy, second.Status);

            transport.Gate.TrySetResult(true);
            var firstResult = await first;
            Assert.Equal(SubscriberCommandStatus.Succeeded, firstResult.Status);
        }

        [Fact]
        public async Task TransportTimeoutIsReportedAndOwnerIsReleased()
        {
            var transport = new RecordingSubscriberTransport { WaitForCancellation = true };
            var service = CreateService(transport, TimeSpan.FromMilliseconds(20));

            var timedOut = await service.ExecuteAsync(Request());
            var retry = await service.ExecuteAsync(Request());

            Assert.Equal(SubscriberCommandStatus.TimedOut, timedOut.Status);
            Assert.Equal(SubscriberCommandStatus.TimedOut, retry.Status);
            Assert.Equal(2, transport.SendCount);
        }

        [Fact]
        public async Task CallerCancellationIsReportedSeparately()
        {
            var transport = new RecordingSubscriberTransport { WaitForCancellation = true };
            var service = CreateService(transport, TimeSpan.FromSeconds(1));
            using var cancellation = new CancellationTokenSource();

            Task<SubscriberCommandResult> pending = service.ExecuteAsync(Request(), cancellation.Token);
            await transport.Started.Task;
            cancellation.Cancel();

            var result = await pending;

            Assert.Equal(SubscriberCommandStatus.Cancelled, result.Status);
        }

        private static SubscriberCommandService CreateService(
            RecordingSubscriberTransport transport,
            TimeSpan? timeout = null)
            => new SubscriberCommandService(
                _ => true,
                _ => transport,
                timeout ?? TimeSpan.FromSeconds(1));

        private static SubscriberCommandRequest Request(
            SubscriberCommandMode mode = SubscriberCommandMode.P25,
            string destinationId = "123456")
            => new SubscriberCommandRequest(
                "commands-window",
                "System A",
                "100001",
                destinationId,
                SubscriberCommandKind.Page,
                mode);

        private sealed class RecordingSubscriberTransport : ISubscriberCommandTransport
        {
            public int SendCount { get; private set; }
            public SubscriberCommandRequest LastRequest { get; private set; } = null!;
            public TaskCompletionSource<bool> Started { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool>? Gate { get; init; }
            public bool WaitForCancellation { get; init; }

            public async Task SendAsync(
                SubscriberCommandRequest request,
                CancellationToken cancellationToken)
            {
                SendCount++;
                LastRequest = request;
                Started.TrySetResult(true);
                if (Gate is not null)
                {
                    await Gate.Task.WaitAsync(cancellationToken);
                }
                else if (WaitForCancellation)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            }
        }
    }
}
