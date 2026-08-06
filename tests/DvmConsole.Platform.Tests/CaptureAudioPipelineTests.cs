// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the transmit-side capture audio pipeline slice
* (plan vertical-slice gate item 6: transmit using in-window PTT):
*
*   DvmConsole.Platform.Audio.CaptureAudioPipeline
*
* The pipeline owns one IAudioInput created through an injected
* IAudioStreamFactory at StartAsync (AudioPcm.Console format), pumps
* captured PCM through the input's push callback, and reassembles
* fragments into exactly AudioPcm.BlockBytes (1600) aligned blocks
* before invoking the caller's onBlock delegate. Fragments may arrive
* in arbitrary sizes (MacAudioInput delivers ~1600-byte chunks, but the
* contract must tolerate any split, e.g. 3x640); the tail shorter than
* a block at stream end is dropped (WPF parity: non-aligned chunks are
* logged and skipped, MainWindow.xaml.cs:3183-3184). The typed
* AudioStreamEnd from the underlying input is returned as-is
* (Requested/Cancelled/DeviceLost/Error). Single-start; a second
* StartAsync throws (parity MacAudioInput). StopAsync idempotent.
* DisposeAsync idempotent, disposes the input.
*
* Capture->monitor loopback is locked by the last test: blocks
* delivered by this pipeline are written by MonitorAudioPipeline,
* proving the vertical-slice receive/transmit plumbing headlessly.
*/
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="CaptureAudioPipeline"/>.
    /// </summary>
    public sealed class CaptureAudioPipelineTests
    {
        /* ------------------------------------------------------------------
        ** Test doubles
        ** ---------------------------------------------------------------- */

        private sealed class FakeAudioInput : IAudioInput
        {
            public AudioDeviceInfo Device { get; }
            public PcmFormat Format { get; }

            public FakeAudioInput(AudioDeviceInfo device, PcmFormat format)
            {
                Device = device;
                Format = format;
            }

            public Func<ReadOnlyMemory<byte>, Task>? OnData;
            public CancellationToken? Token;
            public AudioStreamEnd StartResult = AudioStreamEnd.Requested();
            public AudioDeviceException? ThrowOnStart;
            public int StartCount;
            public int StopCount;

            public Task<AudioStreamEnd> StartAsync(
                Func<ReadOnlyMemory<byte>, Task> onData,
                CancellationToken cancellationToken)
            {
                StartCount++;
                if (ThrowOnStart is { } ex)
                {
                    throw ex;
                }

                OnData = onData;
                Token = cancellationToken;
                return Task.FromResult(StartResult);
            }

            public Task StopAsync()
            {
                StopCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeAudioOutput : IAudioOutput
        {
            public readonly List<byte[]> Writes = new();
            public AudioDeviceInfo Device { get; }
            public PcmFormat Format { get; }

            public FakeAudioOutput(AudioDeviceInfo device, PcmFormat format)
            {
                Device = device;
                Format = format;
            }

            public float Volume { get; set; }
            public int ClearCount { get; private set; }
            public int StopCount { get; private set; }
            public int BufferedBytes { get; set; }
            public AudioWriteStatus NextWriteStatus = AudioWriteStatus.Accepted;

            public AudioWriteResult Write(ReadOnlyMemory<byte> data)
            {
                if (NextWriteStatus == AudioWriteStatus.Accepted)
                {
                    Writes.Add(data.ToArray());
                    BufferedBytes += data.Length;
                }

                return new AudioWriteResult(NextWriteStatus, BufferedBytes);
            }

            public void ClearBuffer()
            {
                ClearCount++;
                BufferedBytes = 0;
            }

            public Task StopAsync()
            {
                StopCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeAudioStreamFactory : IAudioStreamFactory
        {
            public FakeAudioInput? Input;
            public FakeAudioOutput? Output;
            public AudioDeviceException? ThrowOnCreateInput;

            /// <summary>
            /// When set, every created input's StartAsync throws this
            /// exception (models a device that opens but fails to start).
            /// </summary>
            public AudioDeviceException? ThrowOnStartOnCreate;
            public int CreateCount { get; private set; }
            public int DisposeCount { get; private set; }

            public IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format)
            {
                CreateCount++;
                if (ThrowOnCreateInput is { } ex)
                {
                    throw ex;
                }

                var input = new FakeAudioInput(
                    new AudioDeviceInfo(deviceId, AudioDeviceDirection.Input, "Fake Input"),
                    format)
                {
                    ThrowOnStart = ThrowOnStartOnCreate,
                };
                Input = input;
                return input;
            }

            public IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format)
            {
                var output = new FakeAudioOutput(
                    new AudioDeviceInfo(deviceId, AudioDeviceDirection.Output, "Fake Output"),
                    format);
                Output = output;
                return output;
            }

            public IAudioFilePlayer CreateFilePlayer()
                => throw new NotSupportedException();

            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                return ValueTask.CompletedTask;
            }
        }

        /* ------------------------------------------------------------------
        ** Start / device resolution
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task StartAsync_CreatesInputWithRequestedDeviceAndConsoleFormat()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new CaptureAudioPipeline(factory);
            var deviceId = AudioDeviceId.FromKey("input-1");

            var end = await pipeline.StartAsync(
                deviceId, _ => Task.CompletedTask, CancellationToken.None);

            Assert.NotNull(factory.Input);
            Assert.Equal(deviceId, factory.Input!.Device.Id);
            Assert.Equal(AudioPcm.Console, factory.Input.Format);
            Assert.Equal(AudioStreamStopReason.Requested, end.StopReason);
        }

        [Fact]
        public async Task StartAsync_MissingDevice_PropagatesTypedException()
        {
            var factory = new FakeAudioStreamFactory
            {
                ThrowOnCreateInput = new AudioDeviceException(
                    AudioDeviceErrorKind.DeviceUnavailable, "no device"),
            };
            var pipeline = new CaptureAudioPipeline(factory);

            var ex = await Assert.ThrowsAsync<AudioDeviceException>(() =>
                pipeline.StartAsync(AudioDeviceId.Default, _ => Task.CompletedTask, CancellationToken.None));
            Assert.Equal(AudioDeviceErrorKind.DeviceUnavailable, ex.Kind);
        }

        [Fact]
        public async Task StartAsync_StartThrows_IsRetryable()
        {
            // Parity with MacAudioInput (MacAudioInput.cs:86-87): a start
            // that throws must not wedge the pipeline — the caller can fix
            // the condition and start again with a fresh input. The failed
            // input itself is torn down, but the SHARED factory is never
            // disposed by a failed start (the monitor pipeline owns it too,
            // and the retry needs it).
            var factory = new FakeAudioStreamFactory();
            var pipeline = new CaptureAudioPipeline(factory);

            factory.ThrowOnStartOnCreate = new AudioDeviceException(
                AudioDeviceErrorKind.OpenFailed, "device busy");
            await Assert.ThrowsAsync<AudioDeviceException>(() =>
                pipeline.StartAsync(AudioDeviceId.Default, _ => Task.CompletedTask, CancellationToken.None));

            var failedInput = factory.Input!;
            Assert.Equal(1, failedInput.StopCount); // failed input torn down
            Assert.Equal(0, factory.DisposeCount);  // shared factory untouched

            factory.ThrowOnStartOnCreate = null;
            var end = await pipeline.StartAsync(
                AudioDeviceId.Default, _ => Task.CompletedTask, CancellationToken.None);
            Assert.Equal(AudioStreamStopReason.Requested, end.StopReason);
            Assert.Equal(2, factory.CreateCount);
            Assert.Equal(0, factory.DisposeCount); // still untouched after retry
        }

        [Fact]
        public async Task StartAsync_Twice_Throws()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new CaptureAudioPipeline(factory);
            await pipeline.StartAsync(AudioDeviceId.Default, _ => Task.CompletedTask, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pipeline.StartAsync(AudioDeviceId.Default, _ => Task.CompletedTask, CancellationToken.None));
        }

        /* ------------------------------------------------------------------
        ** Block reassembly
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task Fragments_ReassembleIntoExactlyOneBlock()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new CaptureAudioPipeline(factory);
            var blocks = new List<byte[]>();
            await pipeline.StartAsync(AudioDeviceId.Default, block =>
            {
                blocks.Add(block.ToArray());
                return Task.CompletedTask;
            }, CancellationToken.None);

            // 3 x 640-byte fragments = exactly one 1600-byte block.
            await factory.Input!.OnData!(new byte[640]);
            await factory.Input.OnData!(new byte[640]);
            await factory.Input.OnData!(new byte[640]);

            var block = Assert.Single(blocks);
            Assert.Equal(AudioPcm.BlockBytes, block.Length);
        }

        [Fact]
        public async Task CrossBoundaryChunks_ProduceOrderedBlocks()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new CaptureAudioPipeline(factory);
            var blocks = new List<byte[]>();
            await pipeline.StartAsync(AudioDeviceId.Default, block =>
            {
                blocks.Add(block.ToArray());
                return Task.CompletedTask;
            }, CancellationToken.None);

            // 2000 bytes: 1600-aligned block + 400-byte remainder, then a
            // second 1200 bytes completes another block.
            await factory.Input!.OnData!(new byte[2000]);
            await factory.Input.OnData!(new byte[1200]);

            Assert.Equal(2, blocks.Count);
            Assert.All(blocks, b => Assert.Equal(AudioPcm.BlockBytes, b.Length));
        }

        [Fact]
        public async Task PartialTail_IsDroppedOnEnd()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new CaptureAudioPipeline(factory);
            var blocks = new List<byte[]>();
            await pipeline.StartAsync(AudioDeviceId.Default, block =>
            {
                blocks.Add(block.ToArray());
                return Task.CompletedTask;
            }, CancellationToken.None);

            await factory.Input!.OnData!(new byte[1600]);
            await factory.Input.OnData!(new byte[400]); // partial tail

            await pipeline.StopAsync();

            Assert.Single(blocks); // the 400-byte tail never became a block
        }

        /* ------------------------------------------------------------------
        ** Ends / stop / dispose
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task CallbackException_ProducesErrorEnd()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new CaptureAudioPipeline(factory);
            var endTask = pipeline.StartAsync(AudioDeviceId.Default, _ =>
                throw new InvalidOperationException("boom"), CancellationToken.None);

            // The underlying fake returns Requested immediately; the
            // pipeline surfaces the callback failure as an Error end via
            // its own completion semantics. The contract: a throwing
            // callback must never crash the pipeline.
            await factory.Input!.OnData!(new byte[1600]);
            var end = await endTask;

            Assert.Equal(AudioStreamStopReason.Error, end.StopReason);
            Assert.Equal(AudioDeviceErrorKind.Unknown, end.ErrorKind);
        }

        [Fact]
        public async Task PreCancelledToken_ProducesCancelledEnd()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new CaptureAudioPipeline(factory);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var end = await pipeline.StartAsync(AudioDeviceId.Default, _ => Task.CompletedTask, cts.Token);

            Assert.Equal(AudioStreamStopReason.Cancelled, end.StopReason);
        }

        [Fact]
        public async Task StopAsync_RequestedEnd_Idempotent()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new CaptureAudioPipeline(factory);
            await pipeline.StartAsync(AudioDeviceId.Default, _ => Task.CompletedTask, CancellationToken.None);

            await pipeline.StopAsync();
            await pipeline.StopAsync();

            Assert.Equal(2, factory.Input!.StopCount);
        }

        [Fact]
        public async Task DisposeAsync_Idempotent_DisposesInput()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new CaptureAudioPipeline(factory);
            await pipeline.StartAsync(AudioDeviceId.Default, _ => Task.CompletedTask, CancellationToken.None);

            await pipeline.DisposeAsync();
            await pipeline.DisposeAsync();

            Assert.Equal(1, factory.DisposeCount);
        }

        [Fact]
        public void Ctor_NullFactory_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new CaptureAudioPipeline(null!));
        }

        /* ------------------------------------------------------------------
        ** Loopback: capture -> monitor (vertical-slice plumbing)
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task CaptureToMonitorLoopback_DeliversAlignedBlocks()
        {
            // One factory, one input, one output: blocks captured by the
            // capture pipeline flow into the monitor pipeline's writes,
            // proving the receive/transmit audio plumbing headlessly.
            // The monitor uses a long shed threshold so no backlog
            // clearing interferes with the block-count assertions.
            var factory = new FakeAudioStreamFactory();
            var capture = new CaptureAudioPipeline(factory);
            var monitor = new MonitorAudioPipeline(
                factory,
                maxBufferedDuration: TimeSpan.FromSeconds(10));
            monitor.Start(AudioDeviceId.Default);

            var capturedBlocks = 0;
            await capture.StartAsync(AudioDeviceId.Default, block =>
            {
                capturedBlocks++;
                monitor.WritePcm(block);
                return Task.CompletedTask;
            }, CancellationToken.None);

            for (var i = 0; i < 5; i++)
            {
                await factory.Input!.OnData!(new byte[1600]);
            }

            Assert.Equal(5, capturedBlocks);
            Assert.Equal(5, factory.Output!.Writes.Count);
            Assert.All(factory.Output.Writes, w => Assert.Equal(AudioPcm.BlockBytes, w.Length));

            await capture.DisposeAsync();
            await monitor.DisposeAsync();
        }
    }
}
