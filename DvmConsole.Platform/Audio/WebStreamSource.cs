// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLayer;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Configuration for one continuous web-stream monitor source.
    /// </summary>
    public sealed class WebStreamSourceOptions
    {
        public WebStreamSourceOptions(
            string url,
            string? authUsername = null,
            string? authPassword = null,
            TimeSpan? retryDelay = null,
            int maxAttempts = 3,
            int maxEncodedBufferBytes = 64 * 1024)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl))
                throw new ArgumentException("A valid absolute stream URL is required.", nameof(url));
            if (maxAttempts is < 1 or > 3)
                throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Connection attempts must be between one and three.");
            if (maxEncodedBufferBytes < EncodedBytePipe.ChunkBytes)
                throw new ArgumentOutOfRangeException(nameof(maxEncodedBufferBytes));

            Url = parsedUrl;
            AuthUsername = authUsername ?? string.Empty;
            AuthPassword = authPassword ?? string.Empty;
            RetryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
            MaxAttempts = maxAttempts;
            MaxEncodedBufferBytes = maxEncodedBufferBytes;
        }

        public Uri Url { get; }
        public string AuthUsername { get; }
        public string AuthPassword { get; }
        public TimeSpan RetryDelay { get; }
        public int MaxAttempts { get; }
        public int MaxEncodedBufferBytes { get; }
    }

    public enum WebStreamSourceStopReason
    {
        Requested,
        Cancelled,
        Failed,
    }

    public enum WebStreamSourceFailureReason
    {
        Transport,
        UnsupportedFormat,
    }

    public enum WebStreamSourceProgressKind
    {
        Connecting,
        Retry,
        Connected,
    }

    public sealed record WebStreamSourceProgress(
        WebStreamSourceProgressKind Kind,
        int Attempt);

    /// <summary>
    /// Terminal state for a web-stream source run. PCM has already been delivered
    /// through the callback when the result reports a failed or cancelled run.
    /// </summary>
    public sealed class WebStreamSourceResult
    {
        private WebStreamSourceResult(
            WebStreamSourceStopReason stopReason,
            string? errorMessage,
            WebStreamSourceFailureReason? failureReason)
        {
            StopReason = stopReason;
            ErrorMessage = errorMessage;
            FailureReason = failureReason;
        }

        public WebStreamSourceStopReason StopReason { get; }
        public string? ErrorMessage { get; }
        public WebStreamSourceFailureReason? FailureReason { get; }

        internal static WebStreamSourceResult Requested()
            => new(WebStreamSourceStopReason.Requested, null, null);

        internal static WebStreamSourceResult Cancelled()
            => new(WebStreamSourceStopReason.Cancelled, null, null);

        internal static WebStreamSourceResult Failed(
            string message,
            WebStreamSourceFailureReason failureReason)
            => new(WebStreamSourceStopReason.Failed, message, failureReason);
    }

    /// <summary>
    /// Cross-platform web-stream source. HTTP remains asynchronous and owns the
    /// connection lifecycle; NLayer runs synchronously on a worker over a bounded,
    /// forward-only encoded-byte stream. Output is signed 16-bit little-endian
    /// mono PCM in the locked <see cref="AudioPcm.Console"/> format.
    /// </summary>
    public sealed class WebStreamSource : IWebStreamSource
    {
        private readonly WebStreamSourceOptions _options;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly object _stateGate = new();
        private CancellationTokenSource? _runCancellation;
        private Task<WebStreamSourceResult>? _runTask;
        private bool _stopRequested;
        private bool _disposed;

        public WebStreamSource(WebStreamSourceOptions options, HttpClient? httpClient = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _httpClient = httpClient ?? new HttpClient();
            _ownsHttpClient = httpClient is null;
        }

        public Task<WebStreamSourceResult> StartAsync(
            Action<ReadOnlyMemory<byte>> onPcm,
            CancellationToken cancellationToken)
            => StartAsync(onPcm, cancellationToken, null);

        public Task<WebStreamSourceResult> StartAsync(
            Action<ReadOnlyMemory<byte>> onPcm,
            CancellationToken cancellationToken,
            Action<WebStreamSourceProgress>? onProgress)
        {
            if (onPcm is null)
                throw new ArgumentNullException(nameof(onPcm));

            CancellationTokenSource runCancellation;
            TaskCompletionSource<WebStreamSourceResult> completion;
            lock (_stateGate)
            {
                ThrowIfDisposed();
                if (_runCancellation is not null)
                    throw new InvalidOperationException("The web-stream source is already running.");

                runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                completion = new TaskCompletionSource<WebStreamSourceResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _runCancellation = runCancellation;
                _runTask = completion.Task;
                _stopRequested = false;
            }

            _ = CompleteRunAsync(onPcm, onProgress, runCancellation, completion);
            return completion.Task;
        }

        private async Task CompleteRunAsync(
            Action<ReadOnlyMemory<byte>> onPcm,
            Action<WebStreamSourceProgress>? onProgress,
            CancellationTokenSource runCancellation,
            TaskCompletionSource<WebStreamSourceResult> completion)
        {
            WebStreamSourceResult? result = null;
            Exception? failure = null;
            try
            {
                result = await RunAsync(onPcm, onProgress, runCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_runCancellation, runCancellation))
                    {
                        _runCancellation = null;
                        _runTask = null;
                    }
                }

                runCancellation.Dispose();
            }

            if (failure is not null)
                completion.TrySetException(failure);
            else
                completion.TrySetResult(result!);
        }

        public async Task StopAsync()
        {
            Task<WebStreamSourceResult>? runTask;
            lock (_stateGate)
            {
                if (_disposed)
                    return;

                _stopRequested = true;
                runTask = _runTask;
                _runCancellation?.Cancel();
            }

            if (runTask is not null)
                await runTask.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            Task<WebStreamSourceResult>? runTask;
            lock (_stateGate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _stopRequested = true;
                runTask = _runTask;
                _runCancellation?.Cancel();
            }

            if (runTask is not null)
                await runTask.ConfigureAwait(false);
            if (_ownsHttpClient)
                _httpClient.Dispose();
        }

        private async Task<WebStreamSourceResult> RunAsync(
            Action<ReadOnlyMemory<byte>> onPcm,
            Action<WebStreamSourceProgress>? onProgress,
            CancellationToken cancellationToken)
        {
            string? lastFailure = null;
            var lastFailureReason = WebStreamSourceFailureReason.Transport;
            for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return await CancellationResultAsync().ConfigureAwait(false);

                try
                {
                    PublishProgress(onProgress, new WebStreamSourceProgress(
                        attempt == 1
                            ? WebStreamSourceProgressKind.Connecting
                            : WebStreamSourceProgressKind.Retry,
                        attempt));
                    var outcome = await RunAttemptAsync(onPcm, onProgress, attempt, cancellationToken)
                        .ConfigureAwait(false);
                    if (outcome == AttemptOutcome.Completed)
                    {
                        lastFailure = "The stream ended.";
                        lastFailureReason = WebStreamSourceFailureReason.Transport;
                    }
                    else
                    {
                        return await CancellationResultAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return await CancellationResultAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    lastFailure = SafeFailureMessage(exception);
                    lastFailureReason = SafeFailureReason(exception);
                }

                if (attempt < _options.MaxAttempts)
                {
                    try
                    {
                        await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return await CancellationResultAsync().ConfigureAwait(false);
                    }
                }
            }

            return WebStreamSourceResult.Failed(
                lastFailure ?? "The web stream failed.",
                lastFailureReason);
        }

        private async Task<AttemptOutcome> RunAttemptAsync(
            Action<ReadOnlyMemory<byte>> onPcm,
            Action<WebStreamSourceProgress>? onProgress,
            int attempt,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.Url);
            ApplyAuthorization(request);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            PublishProgress(onProgress, new WebStreamSourceProgress(
                WebStreamSourceProgressKind.Connected,
                attempt));

            await using var responseStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var encodedBytes = new EncodedBytePipe(_options.MaxEncodedBufferBytes);
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var producer = PumpResponseAsync(responseStream, encodedBytes, attemptCancellation.Token);
            var decoder = Task.Run(
                () => DecodeAsync(encodedBytes, onPcm, attemptCancellation.Token),
                CancellationToken.None);

            try
            {
                await decoder.ConfigureAwait(false);
                return AttemptOutcome.Completed;
            }
            finally
            {
                attemptCancellation.Cancel();
                encodedBytes.Complete();
                try
                {
                    await producer.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested)
                {
                }
            }
        }

        private async Task PumpResponseAsync(
            Stream responseStream,
            EncodedBytePipe encodedBytes,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[EncodedBytePipe.ChunkBytes];
            try
            {
                while (true)
                {
                    var read = await responseStream.ReadAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        encodedBytes.Complete();
                        return;
                    }

                    await encodedBytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                encodedBytes.Complete(exception);
                throw;
            }
        }

        private static void DecodeAsync(
            EncodedBytePipe encodedBytes,
            Action<ReadOnlyMemory<byte>> onPcm,
            CancellationToken cancellationToken)
        {
            using var decoder = new MpegFile(encodedBytes);
            var normalizer = new PcmNormalizer(decoder.SampleRate, decoder.Channels);
            var samples = new float[4096];
            var pendingPcm = new byte[AudioPcm.FrameBytes * 8];
            var pendingCount = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sampleCount = decoder.ReadSamples(samples, 0, samples.Length);
                if (sampleCount <= 0)
                    break;

                var normalized = normalizer.Process(samples.AsSpan(0, sampleCount), false);
                pendingCount = AppendPcm(normalized, pendingPcm, pendingCount, onPcm);
            }

            var tail = normalizer.Process(ReadOnlySpan<float>.Empty, true);
            _ = AppendPcm(tail, pendingPcm, pendingCount, onPcm, flush: true);
        }

        private static int AppendPcm(
            ReadOnlySpan<float> samples,
            byte[] pendingPcm,
            int pendingCount,
            Action<ReadOnlyMemory<byte>> onPcm,
            bool flush = false)
        {
            foreach (var sample in samples)
            {
                if (pendingCount + 2 > pendingPcm.Length)
                    throw new InvalidOperationException("The normalized PCM buffer is full.");

                var value = (short)Math.Clamp(
                    (int)Math.Round(sample * short.MaxValue),
                    short.MinValue,
                    short.MaxValue);
                pendingPcm[pendingCount++] = (byte)value;
                pendingPcm[pendingCount++] = (byte)(value >> 8);

                var completeBytes = pendingCount - pendingCount % AudioPcm.FrameBytes;
                if (completeBytes > 0)
                {
                    onPcm(pendingPcm.AsMemory(0, completeBytes));
                    var remaining = pendingCount - completeBytes;
                    Buffer.BlockCopy(pendingPcm, completeBytes, pendingPcm, 0, remaining);
                    pendingCount = remaining;
                }
            }

            if (flush && pendingCount > 0)
            {
                Array.Clear(pendingPcm, 0, pendingCount);
                pendingCount = 0;
            }

            return pendingCount;
        }

        private void ApplyAuthorization(HttpRequestMessage request)
        {
            if (string.IsNullOrWhiteSpace(_options.AuthUsername))
                return;

            var credential = _options.AuthUsername + ":" + _options.AuthPassword;
            var encodedCredential = Convert.ToBase64String(Encoding.ASCII.GetBytes(credential));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredential);
        }

        private Task<WebStreamSourceResult> CancellationResultAsync()
        {
            lock (_stateGate)
            {
                var result = _stopRequested
                    ? WebStreamSourceResult.Requested()
                    : WebStreamSourceResult.Cancelled();
                return Task.FromResult(result);
            }
        }

        private static string SafeFailureMessage(Exception exception)
            => exception switch
            {
                HttpRequestException => "The web stream request failed.",
                InvalidDataException => "The web stream audio format is unsupported or invalid.",
                EndOfStreamException => "The web stream ended unexpectedly.",
                _ => "The web stream failed.",
            };

        private static WebStreamSourceFailureReason SafeFailureReason(Exception exception)
            => exception is InvalidDataException
                ? WebStreamSourceFailureReason.UnsupportedFormat
                : WebStreamSourceFailureReason.Transport;

        private static void PublishProgress(
            Action<WebStreamSourceProgress>? onProgress,
            WebStreamSourceProgress progress)
        {
            try
            {
                onProgress?.Invoke(progress);
            }
            catch
            {
                // Progress observers must not fault the transport or decoder.
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WebStreamSource));
        }

        private enum AttemptOutcome
        {
            Completed,
        }
    }


    internal sealed class EncodedBytePipe : Stream, IAsyncDisposable
    {
        internal const int ChunkBytes = 4096;
        private readonly Channel<byte[]> _chunks;
        private byte[]? _current;
        private int _currentOffset;
        private long _position;
        private Exception? _completionError;
        private int _completed;

        internal EncodedBytePipe(int maxBytes)
        {
            // Floor to keep the actual channel storage at or below the
            // configured bound. The producer still splits larger reads into
            // fixed-size chunks, so the unused remainder is not accumulated.
            var capacity = Math.Max(1, maxBytes / ChunkBytes);
            _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < source.Length;)
            {
                var count = Math.Min(ChunkBytes, source.Length - offset);
                var chunk = source.Slice(offset, count).ToArray();
                await _chunks.Writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                offset += count;
            }
        }

        internal void Complete(Exception? error = null)
        {
            if (error is not null)
                Interlocked.CompareExchange(ref _completionError, error, null);
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                _chunks.Writer.TryComplete();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
                return 0;

            while (true)
            {
                if (_current is not null && _currentOffset < _current.Length)
                {
                    var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
                    _current.AsSpan(_currentOffset, count).CopyTo(buffer);
                    _currentOffset += count;
                    _position += count;
                    if (_currentOffset == _current.Length)
                    {
                        _current = null;
                        _currentOffset = 0;
                    }
                    return count;
                }

                if (_chunks.Reader.TryRead(out var next))
                {
                    _current = next;
                    _currentOffset = 0;
                    continue;
                }

                if (!_chunks.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    if (_completionError is not null)
                        ExceptionDispatchInfo.Capture(_completionError).Throw();
                    return 0;
                }
            }
        }

        public override int ReadByte()
        {
            Span<byte> one = stackalloc byte[1];
            return Read(one) == 0 ? -1 : one[0];
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Complete();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Complete();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class PcmNormalizer
    {
        private readonly int _sourceSampleRate;
        private readonly int _sourceChannels;
        private readonly List<float> _monoSamples = new();
        private double _sourcePosition;

        internal PcmNormalizer(int sourceSampleRate, int sourceChannels)
        {
            if (sourceSampleRate <= 0 || sourceChannels <= 0)
                throw new InvalidDataException("The MP3 stream has an invalid PCM format.");

            _sourceSampleRate = sourceSampleRate;
            _sourceChannels = sourceChannels;
        }

        internal float[] Process(ReadOnlySpan<float> interleaved, bool flush)
        {
            for (var index = 0; index + _sourceChannels <= interleaved.Length; index += _sourceChannels)
            {
                var sum = 0.0f;
                for (var channel = 0; channel < _sourceChannels; channel++)
                    sum += interleaved[index + channel];
                _monoSamples.Add(sum / _sourceChannels);
            }

            var output = new List<float>();
            var step = (double)_sourceSampleRate / AudioPcm.Console.SampleRate;
            while (_monoSamples.Count > 0
                && (flush
                    ? _sourcePosition < _monoSamples.Count
                    : _sourcePosition + 1 < _monoSamples.Count))
            {
                var first = (int)_sourcePosition;
                var fraction = _sourcePosition - first;
                var second = Math.Min(first + 1, _monoSamples.Count - 1);
                output.Add(_monoSamples[first] + (float)((_monoSamples[second] - _monoSamples[first]) * fraction));
                _sourcePosition += step;
            }

            var removeCount = Math.Min((int)_sourcePosition, _monoSamples.Count);
            if (removeCount > 0)
            {
                _monoSamples.RemoveRange(0, removeCount);
                _sourcePosition -= removeCount;
            }

            if (flush)
            {
                _monoSamples.Clear();
                _sourcePosition = 0;
            }

            return output.ToArray();
        }
    }
}
