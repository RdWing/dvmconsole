// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// RED contract for the Platform web-stream source.
    ///
    /// The source owns HTTP/authentication, bounded encoded-byte handoff,
    /// MP3 decoding, normalization to AudioPcm.Console, retry, cancellation,
    /// and disposal. UI/session state remains outside this boundary.
    /// </summary>
    public sealed class WebStreamSourceTests
    {
        private static readonly byte[] SampleMp3 = LoadSampleMp3();

        [Fact]
        public async Task StartAsync_SendsBasicAuth_AndNeverExposesPasswordInFailure()
        {
            var handler = new ScriptedHandler((request, _) =>
                throw new HttpRequestException("transport failed"));
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions(
                    "https://radio.example.test/feed",
                    "feed-user",
                    "super-secret",
                    TimeSpan.Zero),
                client);

            var result = await source.StartAsync(_ => { }, CancellationToken.None);

            Assert.Equal(3, handler.Requests.Count);
            var request = handler.Requests[0];
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.NotNull(request.Headers.Authorization?.Parameter);
            Assert.Equal(
                "feed-user:super-secret",
                System.Text.Encoding.ASCII.GetString(
                    Convert.FromBase64String(request.Headers.Authorization!.Parameter!)));
            Assert.Equal(WebStreamSourceStopReason.Failed, result.StopReason);
            Assert.DoesNotContain("super-secret", result.ErrorMessage ?? string.Empty);
        }

        [Fact]
        public async Task StartAsync_RetriesConnectionFailures_AtMostThreeAttempts()
        {
            var handler = new ScriptedHandler((_, _) =>
                throw new HttpRequestException("connection failed"));
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions("https://radio.example.test/feed", retryDelay: TimeSpan.Zero),
                client);

            var result = await source.StartAsync(_ => { }, CancellationToken.None);

            Assert.Equal(WebStreamSourceStopReason.Failed, result.StopReason);
            Assert.Equal(3, handler.Requests.Count);
        }

        [Fact]
        public async Task StartAsync_ReportsConnectionProgressForEachAttempt()
        {
            var requestNumber = 0;
            var handler = new ScriptedHandler((_, _) =>
            {
                requestNumber++;
                if (requestNumber < 3)
                    throw new HttpRequestException("connection failed");

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(SampleMp3),
                };
            });
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions(
                    "https://radio.example.test/feed",
                    retryDelay: TimeSpan.Zero),
                client);
            var progress = new List<WebStreamSourceProgress>();

            var result = await source.StartAsync(
                _ => { },
                CancellationToken.None,
                progress.Add);

            Assert.Equal(WebStreamSourceStopReason.Failed, result.StopReason);
            Assert.Equal(
                new[]
                {
                    WebStreamSourceProgressKind.Connecting,
                    WebStreamSourceProgressKind.Retry,
                    WebStreamSourceProgressKind.Retry,
                    WebStreamSourceProgressKind.Connected,
                },
                progress.Select(item => item.Kind));
            Assert.Equal(new[] { 1, 2, 3, 3 }, progress.Select(item => item.Attempt));
        }

        [Fact]
        public async Task StartAsync_DecodesMp3ToConsolePcm_AndReconnectsAfterEof()
        {
            var handler = new ScriptedHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(SampleMp3),
                });
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions("https://radio.example.test/feed", retryDelay: TimeSpan.Zero),
                client);
            var chunks = new List<byte[]>();

            var result = await source.StartAsync(
                pcm => chunks.Add(pcm.ToArray()),
                CancellationToken.None);

            Assert.Equal(WebStreamSourceStopReason.Failed, result.StopReason);
            Assert.Equal(3, handler.Requests.Count);
            Assert.NotEmpty(chunks);
            Assert.All(chunks, chunk =>
            {
                Assert.NotEmpty(chunk);
                Assert.True(AudioPcm.IsFrameAligned(chunk.Length));
            });
        }

        [Fact]
        public async Task StartAsync_48KhzStereoMp3_DownmixesAndResamplesToConsolePcm()
        {
            var stereoMp3 = File.ReadAllBytes(Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "web-stream-stereo-48k.mp3"));
            var handler = new ScriptedHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(stereoMp3),
                });
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions(
                    "https://radio.example.test/feed",
                    retryDelay: TimeSpan.Zero,
                    maxAttempts: 1),
                client);
            var chunks = new List<byte[]>();

            await source.StartAsync(pcm => chunks.Add(pcm.ToArray()), CancellationToken.None);

            var byteCount = chunks.Sum(chunk => chunk.Length);
            Assert.True(byteCount > 10_000);
            Assert.True(byteCount < 40_000);
            Assert.All(chunks, chunk => Assert.True(AudioPcm.IsFrameAligned(chunk.Length)));
        }

        [Fact]
        public async Task StartAsync_44100HzMp3_ResamplesWithoutTailFailure()
        {
            var mp3 = File.ReadAllBytes(Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "web-stream-44k1.mp3"));
            var handler = new ScriptedHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(mp3),
                });
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions(
                    "https://radio.example.test/feed",
                    retryDelay: TimeSpan.Zero,
                    maxAttempts: 1),
                client);
            var chunks = new List<byte[]>();

            await source.StartAsync(pcm => chunks.Add(pcm.ToArray()), CancellationToken.None);

            Assert.All(chunks, chunk => Assert.True(AudioPcm.IsFrameAligned(chunk.Length)));
        }

        [Fact]
        public async Task StartAsync_8KhzMp3_PreservesConsoleSampleRateAndFrames()
        {
            var mp3 = File.ReadAllBytes(Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "web-stream-8k.mp3"));
            var handler = new ScriptedHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(mp3),
                });
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions(
                    "https://radio.example.test/feed",
                    retryDelay: TimeSpan.Zero,
                    maxAttempts: 1),
                client);
            var chunks = new List<byte[]>();

            await source.StartAsync(pcm => chunks.Add(pcm.ToArray()), CancellationToken.None);

            Assert.True(chunks.Sum(chunk => chunk.Length) > 10_000);
            Assert.All(chunks, chunk => Assert.True(AudioPcm.IsFrameAligned(chunk.Length)));
        }

        [Fact]
        public async Task StartAsync_ProducesPcmBeforeHttpBodyReachesEof()
        {
            var body = new GatedMp3Stream(SampleMp3);
            var handler = new ScriptedHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(body),
                });
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions("https://radio.example.test/feed", retryDelay: TimeSpan.Zero),
                client);
            var firstPcm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var run = source.StartAsync(
                pcm =>
                {
                    if (!pcm.IsEmpty)
                        firstPcm.TrySetResult(true);
                },
                cancellation.Token);

            await body.FirstChunkSent.WaitAsync(TimeSpan.FromSeconds(2));
            await firstPcm.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(body.BodyCompleted.IsCompleted);

            body.ReleaseBody();
            await run;
        }

        [Fact]
        public async Task StartAsync_CancellationDuringRead_StopsWithoutRetrying()
        {
            var body = new BlockingReadStream();
            var handler = new ScriptedHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(body),
                });
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions("https://radio.example.test/feed", retryDelay: TimeSpan.Zero),
                client);
            using var cancellation = new CancellationTokenSource();

            var run = source.StartAsync(_ => { }, cancellation.Token);
            await body.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(WebStreamSourceStopReason.Cancelled, result.StopReason);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task StartAsync_CancellationDuringConnect_StopsWithoutRetrying()
        {
            using var handler = new BlockingConnectHandler();
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions("https://radio.example.test/feed", retryDelay: TimeSpan.Zero),
                client);
            using var cancellation = new CancellationTokenSource();

            var run = source.StartAsync(_ => { }, cancellation.Token);
            await handler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(WebStreamSourceStopReason.Cancelled, result.StopReason);
            Assert.Equal(1, handler.Attempts);
        }

        [Fact]
        public async Task StopAsync_CancelsAnActiveRead_AndIsIdempotent()
        {
            var body = new BlockingReadStream();
            var handler = new ScriptedHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(body),
                });
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions("https://radio.example.test/feed", retryDelay: TimeSpan.Zero),
                client);

            var run = source.StartAsync(_ => { }, CancellationToken.None);
            await body.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));

            await source.StopAsync();
            await source.StopAsync();
            var result = await run;

            Assert.Equal(WebStreamSourceStopReason.Requested, result.StopReason);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task StopAsync_RacingImmediateCompletion_DoesNotThrowAfterCancellationSourceDispose()
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var handler = new ScriptedHandler((_, _) =>
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(new byte[64]),
                    });
                using var client = new HttpClient(handler);
                await using var source = new WebStreamSource(
                    new WebStreamSourceOptions("https://radio.example.test/feed", retryDelay: TimeSpan.Zero),
                    client);

                var run = source.StartAsync(_ => { }, CancellationToken.None);
                var stop = source.StopAsync();
                await Task.WhenAll(run, stop);
            }
        }

        [Fact]
        public async Task DisposeAsync_CancelsAnActiveRead_AndPreventsRestart()
        {
            var body = new BlockingReadStream();
            var handler = new ScriptedHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(body),
                });
            using var client = new HttpClient(handler);
            var source = new WebStreamSource(
                new WebStreamSourceOptions("https://radio.example.test/feed", retryDelay: TimeSpan.Zero),
                client);

            var run = source.StartAsync(_ => { }, CancellationToken.None);
            await body.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));

            await source.DisposeAsync();
            var result = await run;

            Assert.Equal(WebStreamSourceStopReason.Requested, result.StopReason);
            Assert.Throws<ObjectDisposedException>(() =>
            {
                _ = source.StartAsync(_ => { }, CancellationToken.None);
            });
            await source.DisposeAsync();
        }

        [Fact]
        public async Task StartAsync_MalformedMp3_FailsAfterThreeDecoderAttempts()
        {
            var handler = new ScriptedHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[64]),
                });
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions("https://radio.example.test/feed", retryDelay: TimeSpan.Zero),
                client);

            var result = await source.StartAsync(_ => { }, CancellationToken.None);

            Assert.Equal(WebStreamSourceStopReason.Failed, result.StopReason);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(WebStreamSourceFailureReason.UnsupportedFormat, result.FailureReason);
            Assert.DoesNotContain("super-secret", result.ErrorMessage ?? string.Empty);
        }

        [Fact]
        public async Task StartAsync_MidBodyTransportFailure_RetriesAndReturnsTypedFailure()
        {
            var handler = new ScriptedHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new FailingReadStream()),
                });
            using var client = new HttpClient(handler);
            await using var source = new WebStreamSource(
                new WebStreamSourceOptions("https://radio.example.test/feed", retryDelay: TimeSpan.Zero),
                client);

            var result = await source.StartAsync(_ => { }, CancellationToken.None);

            Assert.Equal(WebStreamSourceStopReason.Failed, result.StopReason);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(WebStreamSourceFailureReason.Transport, result.FailureReason);
            Assert.DoesNotContain("secret", result.ErrorMessage ?? string.Empty);
        }

        [Fact]
        public void Factory_DisposePreventsNewSources()
        {
            using var client = new HttpClient(new ScriptedHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            var factory = new WebStreamSourceFactory(client);
            factory.Dispose();

            Assert.Throws<ObjectDisposedException>(() => factory.Create(
                new WebStreamSourceOptions("https://radio.example.test/feed")));
            factory.Dispose();
        }

        [Fact]
        public void Options_RejectsMoreThanThreeConnectionAttempts()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new WebStreamSourceOptions(
                    "https://radio.example.test/feed",
                    maxAttempts: 4));
        }

        private static byte[] LoadSampleMp3()
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "web-stream-sample.mp3");
            return File.ReadAllBytes(path);
        }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

            public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            public List<HttpRequestMessage> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(_handler(request, cancellationToken));
            }
        }

        private sealed class BlockingConnectHandler : HttpMessageHandler
        {
            private readonly TaskCompletionSource<bool> _requestStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task RequestStarted => _requestStarted.Task;
            public int Attempts { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Attempts++;
                _requestStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        }

        private sealed class BlockingReadStream : Stream
        {
            private readonly TaskCompletionSource<bool> _readStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task ReadStarted => _readStarted.Task;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => 0;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
                => throw new NotSupportedException();

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
                => WaitForCancellationAsync(cancellationToken);

            private async ValueTask<int> WaitForCancellationAsync(CancellationToken cancellationToken)
            {
                _readStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class FailingReadStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => 0;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
                => throw new HttpRequestException("connection reset");

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
                => ValueTask.FromException<int>(new HttpRequestException("connection reset"));

            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class GatedMp3Stream : Stream
        {
            private readonly byte[] _payload;
            private readonly TaskCompletionSource<bool> _firstChunkSent =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _bodyCompleted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _offset;

            public GatedMp3Stream(byte[] payload)
            {
                _payload = payload;
            }

            public Task FirstChunkSent => _firstChunkSent.Task;
            public Task BodyCompleted => _bodyCompleted.Task;
            public void ReleaseBody() => _release.TrySetResult(true);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _payload.Length;
            public override long Position
            {
                get => _offset;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
                => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (_offset == 0)
                {
                    var firstCount = Math.Min(4096, _payload.Length);
                    _payload.AsMemory(0, firstCount).CopyTo(buffer);
                    _offset = firstCount;
                    _firstChunkSent.TrySetResult(true);
                    return firstCount;
                }

                await _release.Task.WaitAsync(cancellationToken);
                if (_offset >= _payload.Length)
                {
                    _bodyCompleted.TrySetResult(true);
                    return 0;
                }

                var count = Math.Min(buffer.Length, _payload.Length - _offset);
                _payload.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                if (_offset >= _payload.Length)
                    _bodyCompleted.TrySetResult(true);
                return count;
            }

            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
