// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Transmit-side capture audio pipeline. Owns one capture stream created
    /// through an injected <see cref="IAudioStreamFactory"/> at
    /// <see cref="StartAsync"/> (console PCM format) and reassembles the
    /// input's PCM fragments into exactly <see cref="AudioPcm.BlockBytes"/>
    /// (1600) aligned blocks before invoking the caller's onBlock delegate.
    /// Fragments may arrive in arbitrary sizes; the tail shorter than a block
    /// at stream end is dropped. The typed end from the underlying input is
    /// returned as-is (Requested/Cancelled/DeviceLost/Error), except a
    /// throwing onBlock surfaces as an <see cref="AudioStreamStopReason.Error"/>
    /// end through the pipeline's own completion and never crashes the pump.
    /// </summary>
    public sealed class CaptureAudioPipeline : IAsyncDisposable
    {
        private readonly IAudioStreamFactory _factory;
        private readonly object _stateGate = new();
        private readonly byte[] _blockBuffer = new byte[AudioPcm.BlockBytes];
        private int _bufferedCount;
        private IAudioInput? _input;

        /// <summary>
        /// The pipeline's own end task (the task returned by
        /// <see cref="StartAsync"/>): awaits the underlying input's end
        /// task and the pump completion. Stored so stop/dispose can join
        /// the in-flight pump after requesting the input stop.
        /// </summary>
        private Task<AudioStreamEnd>? _endTask;
        private int _disposed;

        /// <summary>
        /// Creates a capture pipeline over the given stream factory.
        /// </summary>
        /// <param name="factory">Factory that creates the capture stream at StartAsync.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is null.</exception>
        public CaptureAudioPipeline(IAudioStreamFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>
        /// Creates the capture stream for the requested device and starts
        /// pumping fragments, delivering exactly <see cref="AudioPcm.BlockBytes"/>
        /// aligned blocks to <paramref name="onBlock"/>.
        /// </summary>
        /// <param name="inputDeviceId">Device to capture audio from.</param>
        /// <param name="onBlock">Callback receiving each aligned PCM block.</param>
        /// <param name="cancellationToken">
        /// Cancels the stream, producing a Cancelled end. Checked first: a
        /// pre-cancelled token ends the stream without creating an input.
        /// </param>
        /// <returns>
        /// A task completing with the typed end of the stream. A throwing
        /// <paramref name="onBlock"/> produces an
        /// <see cref="AudioStreamStopReason.Error"/> end (Unknown kind) via
        /// the pipeline's own completion instead of crashing the pump.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="onBlock"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the pipeline is already started (single-start, parity
        /// MacAudioInput).
        /// </exception>
        /// <exception cref="AudioDeviceException">
        /// Thrown, typed, when the input device is unavailable.
        /// </exception>
        public Task<AudioStreamEnd> StartAsync(
            AudioDeviceId inputDeviceId,
            Func<ReadOnlyMemory<byte>, Task> onBlock,
            CancellationToken cancellationToken)
        {
            if (onBlock is null)
            {
                throw new ArgumentNullException(nameof(onBlock));
            }

            // Cancellation is checked before anything else: a pre-cancelled
            // token ends the stream without creating an input.
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(AudioStreamEnd.Cancelled());
            }

            lock (_stateGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(CaptureAudioPipeline));
                }

                if (_input is not null)
                {
                    throw new InvalidOperationException(
                        "The capture audio pipeline can only be started once.");
                }

                var input = _factory.CreateInput(inputDeviceId, AudioPcm.Console);

                var completion = new TaskCompletionSource<AudioStreamEnd>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                try
                {
                    var endTask = input.StartAsync(
                        data => PumpAsync(data, onBlock, completion),
                        cancellationToken);
                    var pipelineEnd = AwaitEndAsync(endTask, completion);
                    _input = input;
                    _endTask = pipelineEnd;
                    return pipelineEnd;
                }
                catch
                {
                    // A throwing start must not wedge the pipeline (parity
                    // MacAudioInput.cs:86-87, which resets its started flag
                    // and releases native resources on failure): dispose the
                    // failed input, clear the fields and rethrow so a later
                    // StartAsync retries with a fresh input. The shared
                    // factory is deliberately left alone — it is owned
                    // jointly with MonitorAudioPipeline and disposing it here
                    // would break the monitor's live output and make retry
                    // impossible.
                    _input = null;
                    _endTask = null;
                    try
                    {
                        if (input is IAsyncDisposable disposable)
                        {
                            disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                        else
                        {
                            input.StopAsync().GetAwaiter().GetResult();
                        }
                    }
                    catch
                    {
                        // Disposal failures must never mask the original
                        // start failure.
                    }

                    throw;
                }
            }
        }

        /// <summary>
        /// Stops the capture stream and joins the in-flight pump: the
        /// returned task completes only once the input stop has been
        /// requested AND the pipeline's own end task (input end plus pump
        /// completion) has finished, so no voice block can be delivered
        /// after this returns. Idempotent; a pipeline that never started
        /// completes immediately.
        /// </summary>
        public async Task StopAsync()
        {
            var input = Volatile.Read(ref _input);
            if (input is not null)
            {
                await input.StopAsync().ConfigureAwait(false);
            }

            if (Volatile.Read(ref _endTask) is { } endTask)
            {
                await endTask.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Stops and joins the capture stream (same stop/join path as
        /// <see cref="StopAsync"/>), disposes it and disposes the injected
        /// factory. Idempotent.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (Volatile.Read(ref _input) is { } input)
            {
                await input.StopAsync().ConfigureAwait(false);
                if (Volatile.Read(ref _endTask) is { } endTask)
                {
                    await endTask.ConfigureAwait(false);
                }
                if (input is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync().ConfigureAwait(false);
                }
            }

            await _factory.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Reassembles one input fragment into <see cref="AudioPcm.BlockBytes"/>
        /// aligned blocks, invoking <paramref name="onBlock"/> once per
        /// complete block. A partial tail stays buffered until the next
        /// fragment completes it; a tail shorter than a block at stream end is
        /// simply never emitted. A throwing <paramref name="onBlock"/> is
        /// caught and surfaced as an Error end through the pipeline's own
        /// completion; the pump stays alive.
        /// </summary>
        private async Task PumpAsync(
            ReadOnlyMemory<byte> fragment,
            Func<ReadOnlyMemory<byte>, Task> onBlock,
            TaskCompletionSource<AudioStreamEnd> completion)
        {
            try
            {
                var remaining = fragment;
                while (remaining.Length > 0)
                {
                    var take = Math.Min(AudioPcm.BlockBytes - _bufferedCount, remaining.Length);
                    remaining.Slice(0, take).Span.CopyTo(_blockBuffer.AsSpan(_bufferedCount, take));
                    _bufferedCount += take;
                    remaining = remaining.Slice(take);

                    if (_bufferedCount == AudioPcm.BlockBytes)
                    {
                        await onBlock(new ReadOnlyMemory<byte>(_blockBuffer)).ConfigureAwait(false);
                        _bufferedCount = 0;
                    }
                }
            }
            catch (Exception exception)
            {
                completion.TrySetResult(
                    AudioStreamEnd.Error(AudioDeviceErrorKind.Unknown, exception.Message));
            }
        }

        /// <summary>
        /// Completes the pipeline's own end task with the underlying input's
        /// typed end, unless a pump failure already completed it with an Error
        /// end (which then wins). A short settle delay precedes the input-end
        /// observation so pump activity the input started around its end (for
        /// example a fragment delivered just before the end task completes) is
        /// allowed to finish first: a failure in that window must never be
        /// masked by the input's end. The delay only affects how promptly the
        /// end task finalizes, never the pump itself.
        /// </summary>
        private static async Task<AudioStreamEnd> AwaitEndAsync(
            Task<AudioStreamEnd> endTask,
            TaskCompletionSource<AudioStreamEnd> completion)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1));
            var end = await endTask.ConfigureAwait(false);
            completion.TrySetResult(end);
            return await completion.Task.ConfigureAwait(false);
        }
    }
}
