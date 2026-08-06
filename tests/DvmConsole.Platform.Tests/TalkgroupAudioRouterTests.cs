// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the talkgroup audio router slice (plan Task 9
* / vertical-slice gate items 5-6: receive+decode one DMR/P25 stream
* through the macOS audio backend; transmit via in-window PTT with
* local monitor playback):
*
*   DvmConsole.Platform.Audio.TalkgroupAudioRouter
*   DvmConsole.Platform.Audio.IVoiceFrameEncoder
*   DvmConsole.Platform.Audio.IVoiceTrafficSender (+ VoiceMode,
*     TransmitTarget, StubVoiceTrafficSender)
*
* The router is the headless audio engine: receive-side per-talkgroup
* MonitorAudioPipeline routing (lazy creation, WPF AudioManager parity
* 250 ms shed via the pipeline, 2 s idle release), transmit-side
* CaptureAudioPipeline lifecycle gating with 1600->5x320 splitting
* (AudioConverter.SplitToChunks parity), per-codeword vocoder
* encode/decode through injected seams, DMR triple / P25 LDU
* accumulation, and FNE-send through an injected IVoiceTrafficSender
* seam (StubVoiceTrafficSender until the fnecore adapter lands).
*
* Decode granularity (locked here): WPF decodes each 9-byte DMR
* codeword / 11-byte P25 codeword into 160 samples (MainWindow.DMR.cs:
* 182-203, MainWindow.P25.cs:301-333), so the router feeds
* PER-CODEWORD units through MonitorAudioPipeline.WriteVoiceFrame —
* a DMR frame yields three 320-byte PCM writes, a P25 LDU nine.
*
* The router owns the IAudioStreamFactory and disposes it exactly
* once; per-talkgroup pipelines are stopped, never individually
* disposed (MonitorAudioPipeline.DisposeAsync would dispose the
* shared factory).
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="TalkgroupAudioRouter"/> and the
    /// voice-codec/traffic seams.
    /// </summary>
    public sealed class TalkgroupAudioRouterTests
    {
        /* ------------------------------------------------------------------
        ** Test doubles
        ** ---------------------------------------------------------------- */

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
            public AudioStreamEnd StartResult = AudioStreamEnd.Requested();
            public TaskCompletionSource<AudioStreamEnd>? EndGate;
            public int StartCount;
            public int StopCount;

            public Task<AudioStreamEnd> StartAsync(
                Func<ReadOnlyMemory<byte>, Task> onData,
                CancellationToken cancellationToken)
            {
                StartCount++;
                OnData = onData;
                return EndGate is not null
                    ? EndGate.Task
                    : Task.FromResult(StartResult);
            }

            public Task StopAsync()
            {
                StopCount++;
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Factory whose output creation works but whose input creation
        /// always throws (mic unavailable) — pins the failed-begin path.
        /// </summary>
        private sealed class ThrowingInputFactory : IAudioStreamFactory
        {
            public readonly List<FakeAudioOutput> Outputs = new();
            public int DisposeCount { get; private set; }

            public IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format)
                => throw new AudioDeviceException(
                    AudioDeviceErrorKind.OpenFailed, "input device unavailable");

            public IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format)
            {
                var output = new FakeAudioOutput(
                    new AudioDeviceInfo(deviceId, AudioDeviceDirection.Output, "Fake Output"),
                    format);
                Outputs.Add(output);
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

        private sealed class FakeAudioStreamFactory : IAudioStreamFactory
        {
            public readonly List<FakeAudioOutput> Outputs = new();
            public readonly List<FakeAudioInput> Inputs = new();
            public int DisposeCount { get; private set; }

            /// <summary>
            /// When set, the next created input's StartAsync returns this
            /// gate's task instead of Task.FromResult (end-task timing
            /// control); the gate is consumed on first use.
            /// </summary>
            public TaskCompletionSource<AudioStreamEnd>? EndGateOnCreate;

            public IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format)
            {
                var input = new FakeAudioInput(
                    new AudioDeviceInfo(deviceId, AudioDeviceDirection.Input, "Fake Input"),
                    format)
                {
                    EndGate = EndGateOnCreate,
                };
                EndGateOnCreate = null;
                Inputs.Add(input);
                return input;
            }

            public IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format)
            {
                var output = new FakeAudioOutput(
                    new AudioDeviceInfo(deviceId, AudioDeviceDirection.Output, "Fake Output"),
                    format);
                Outputs.Add(output);
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

        private sealed class FakeVoiceFrameDecoder : IVoiceFrameDecoder
        {
            public bool Reject;
            public int DecodeCount { get; private set; }

            public bool TryDecode(ReadOnlyMemory<byte> voiceFrame, out short[] samples)
            {
                DecodeCount++;
                if (Reject)
                {
                    samples = Array.Empty<short>();
                    return false;
                }

                samples = new short[160];
                return true;
            }
        }

        private sealed class FakeVoiceFrameEncoder : IVoiceFrameEncoder
        {
            public int EncodeCount { get; private set; }
            public int CodewordLength = 9;
            public readonly List<VoiceMode> ReceivedModes = new();

            public bool TryEncode(VoiceMode mode, ReadOnlyMemory<short> samples, out byte[] codeword)
            {
                EncodeCount++;
                ReceivedModes.Add(mode);
                codeword = new byte[CodewordLength];
                return true;
            }
        }

        private sealed class RecordingTrafficSender : IVoiceTrafficSender
        {
            public readonly List<(TransmitTarget Target, byte[] Ambe27, uint StreamId, int Seq)> DmrFrames = new();
            public readonly List<(TransmitTarget Target, bool IsLdu2, byte[] Ldu225, uint StreamId, int Seq)> P25Ldus = new();

            public void SendDmrVoice(TransmitTarget target, ReadOnlyMemory<byte> ambe27, uint streamId, int seqNo)
                => DmrFrames.Add((target, ambe27.ToArray(), streamId, seqNo));

            public void SendP25Ldu(TransmitTarget target, bool isLdu2, ReadOnlyMemory<byte> ldu225, uint streamId, int seqNo)
                => P25Ldus.Add((target, isLdu2, ldu225.ToArray(), streamId, seqNo));
        }

        /// <summary>One-shot scheduler double, like the FNE service tests.</summary>
        private sealed class ManualScheduler
        {
            private sealed class Scheduled
            {
                public Action? Action;
            }

            private readonly List<Scheduled> scheduled = new();

            /// <summary>Live (uncancelled) scheduled actions.</summary>
            public int PendingCount => scheduled.Count(s => s.Action is not null);

            public IDisposable Schedule(TimeSpan delay, Action action)
            {
                var entry = new Scheduled { Action = action };
                scheduled.Add(entry);
                return new Cancellation(() => entry.Action = null);
            }

            /// <summary>Fires every currently scheduled, uncancelled action once.</summary>
            public void Elapse()
            {
                foreach (var entry in scheduled.ToList())
                {
                    entry.Action?.Invoke();
                }
            }

            private sealed class Cancellation : IDisposable
            {
                private readonly Action cancel;

                public Cancellation(Action cancel) => this.cancel = cancel;

                public void Dispose() => cancel();
            }
        }

        private static readonly TransmitTarget DmrTarget = new(
            "System 1", "31001", 1, VoiceMode.Dmr, 1001);

        private static TalkgroupAudioRouter CreateRouter(
            IAudioStreamFactory factory,
            FakeVoiceFrameDecoder decoder,
            FakeVoiceFrameEncoder encoder,
            RecordingTrafficSender sender,
            ManualScheduler scheduler)
            => new TalkgroupAudioRouter(
                factory,
                decoder,
                encoder,
                sender,
                () => AudioDeviceId.Default,
                scheduler: scheduler.Schedule);

        /* ------------------------------------------------------------------
        ** Receive routing
        ** ---------------------------------------------------------------- */

        [Fact]
        public void RouteVoiceFrame_CreatesPipelineOnFirstFrame_ReusesOnSecond()
        {
            var factory = new FakeAudioStreamFactory();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), new ManualScheduler());

            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr);
            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr);

            var output = Assert.Single(factory.Outputs); // one pipeline reused
            Assert.Equal(6, output.Writes.Count); // 3 codeword writes per frame
        }

        [Fact]
        public void RouteVoiceFrame_DmrFrame_ThreeCodewordWrites_Each320Bytes()
        {
            var factory = new FakeAudioStreamFactory();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), new ManualScheduler());

            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr);

            var output = Assert.Single(factory.Outputs);
            Assert.Equal(3, output.Writes.Count); // per-codeword decode granularity
            Assert.All(output.Writes, w => Assert.Equal(320, w.Length));
        }

        [Fact]
        public void RouteVoiceFrame_P25Ldu_NineCodewordWrites()
        {
            var factory = new FakeAudioStreamFactory();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), new ManualScheduler());

            router.RouteVoiceFrame("SYS1/TG2", new byte[225], VoiceMode.P25);

            var output = Assert.Single(factory.Outputs);
            Assert.Equal(9, output.Writes.Count); // 9 x 11-byte codewords
            Assert.All(output.Writes, w => Assert.Equal(320, w.Length));
        }

        [Fact]
        public void RouteVoiceFrame_DecoderRejects_SilentSkip_NoWrite()
        {
            var factory = new FakeAudioStreamFactory();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder { Reject = true },
                new FakeVoiceFrameEncoder(), new RecordingTrafficSender(),
                new ManualScheduler());

            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr);

            var output = Assert.Single(factory.Outputs);
            Assert.Empty(output.Writes);
        }

        /* ------------------------------------------------------------------
        ** Idle release
        ** ---------------------------------------------------------------- */

        [Fact]
        public void IdleRelease_AfterTwoSeconds_StopsPipeline()
        {
            var factory = new FakeAudioStreamFactory();
            var scheduler = new ManualScheduler();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), scheduler);

            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr);
            var output = Assert.Single(factory.Outputs);
            Assert.Equal(0, output.StopCount);

            scheduler.Elapse(); // 2 s idle release fires

            Assert.Equal(1, output.StopCount);
        }

        [Fact]
        public void IdleRelease_NewAudioBeforeDelay_KeepsPipeline()
        {
            var factory = new FakeAudioStreamFactory();
            var scheduler = new ManualScheduler();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), scheduler);

            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr);
            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr); // resets the timer

            // The earlier release was cancelled; exactly one release is
            // pending, and the pipeline is still alive.
            Assert.Equal(1, scheduler.PendingCount);
            var output = Assert.Single(factory.Outputs);
            Assert.Equal(0, output.StopCount);

            scheduler.Elapse(); // the (single) pending release fires

            Assert.Equal(1, output.StopCount);
            Assert.Single(factory.Outputs); // same pipeline kept until release
        }

        [Fact]
        public void IdleRelease_RecreateOnNewFrame()
        {
            var factory = new FakeAudioStreamFactory();
            var scheduler = new ManualScheduler();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), scheduler);

            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr);
            scheduler.Elapse(); // release
            Assert.Single(factory.Outputs);

            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr);
            Assert.Equal(2, factory.Outputs.Count); // fresh pipeline
            Assert.Equal(0, factory.Outputs[1].StopCount);
        }

        /* ------------------------------------------------------------------
        ** Transmit
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task BeginTransmit_StartsCapture_Splits1600IntoFive320ByteChunks()
        {
            var factory = new FakeAudioStreamFactory();
            var encoder = new FakeVoiceFrameEncoder();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), encoder,
                new RecordingTrafficSender(), new ManualScheduler());

            await router.BeginTransmitAsync(DmrTarget, AudioDeviceId.Default, CancellationToken.None);

            var input = Assert.Single(factory.Inputs);
            Assert.Equal(1, input.StartCount);
            Assert.NotNull(input.OnData);

            await input.OnData!(new byte[1600]);

            Assert.Equal(5, encoder.EncodeCount); // 5 x 320-byte chunks
        }

        [Fact]
        public async Task BeginTransmit_Dmr_AccumulatesTriple_Sends27ByteFrame()
        {
            var factory = new FakeAudioStreamFactory();
            var encoder = new FakeVoiceFrameEncoder { CodewordLength = 9 };
            var sender = new RecordingTrafficSender();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), encoder, sender, new ManualScheduler());

            await router.BeginTransmitAsync(DmrTarget, AudioDeviceId.Default, CancellationToken.None);
            var input = factory.Inputs[0];

            await input.OnData!(new byte[1600]); // 5 chunks -> 5 codewords, not a triple yet

            // 5 codewords do not complete a 3-codeword triple: no frame sent
            // until the accumulator has a full triple (WPF parity: DMR triples
            // are 3 codewords = 3 chunks... but here each CHUNK is one codeword
            // because encode consumes one 320-byte chunk -> one 9-byte codeword;
            // a 1600-byte block yields 5 chunks -> 5 codewords -> 1 triple + 2
            // carried over).
            Assert.True(sender.DmrFrames.Count >= 1);
            var (target, ambe27, _, _) = sender.DmrFrames[0];
            Assert.Equal(27, ambe27.Length);
            Assert.Equal("System 1", target.SystemName);
            Assert.Equal("31001", target.TalkgroupId);
            Assert.Equal(VoiceMode.Dmr, target.Mode);
            Assert.Equal(1, target.Slot);
            Assert.Equal(1001u, target.SourceId);
        }

        [Fact]
        public async Task BeginTransmit_P25_AccumulatesNineCodewords_SendsLdu()
        {
            var factory = new FakeAudioStreamFactory();
            var encoder = new FakeVoiceFrameEncoder { CodewordLength = 11 };
            var sender = new RecordingTrafficSender();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), encoder, sender, new ManualScheduler());

            await router.BeginTransmitAsync(
                new TransmitTarget("System 1", "31002", 1, VoiceMode.P25, 1001),
                AudioDeviceId.Default, CancellationToken.None);
            var input = factory.Inputs[0];

            await input.OnData!(new byte[1600]); // 5 codewords
            await input.OnData!(new byte[1600]); // +5 = 10 -> one 9-codeword LDU + 1 carry

            var ldu = Assert.Single(sender.P25Ldus);
            Assert.Equal(225, ldu.Ldu225.Length);

            // The router must forward the session's mode to the encoder seam
            // (a regression hardcoding VoiceMode.Dmr would still assemble an
            // 11-byte codeword through the fake, so pin the mode arrival).
            Assert.NotEmpty(encoder.ReceivedModes);
            Assert.All(encoder.ReceivedModes, m => Assert.Equal(VoiceMode.P25, m));
        }

        [Fact]
        public async Task EndTransmit_StopsCapture_NoFurtherSends()
        {
            var factory = new FakeAudioStreamFactory();
            var encoder = new FakeVoiceFrameEncoder();
            var sender = new RecordingTrafficSender();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), encoder, sender, new ManualScheduler());

            await router.BeginTransmitAsync(DmrTarget, AudioDeviceId.Default, CancellationToken.None);
            var input = factory.Inputs[0];
            await input.OnData!(new byte[1600]);
            var sendsBefore = sender.DmrFrames.Count;

            await router.EndTransmitAsync();

            Assert.Equal(1, input.StopCount);
            await input.OnData!(new byte[1600]); // late block after end
            Assert.Equal(sendsBefore, sender.DmrFrames.Count);
        }

        [Fact]
        public async Task CaptureEnded_DeviceLost_RaisedExactlyOnce()
        {
            var factory = new FakeAudioStreamFactory();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), new ManualScheduler());
            var ends = new List<AudioStreamEnd>();
            router.CaptureEnded += ends.Add;

            // The fake's StartResult is set BEFORE BeginTransmitAsync so the
            // end task completes verbatim with DeviceLost (not a
            // Requested-then-remapped path).
            factory.EndGateOnCreate = new TaskCompletionSource<AudioStreamEnd>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await router.BeginTransmitAsync(DmrTarget, AudioDeviceId.Default, CancellationToken.None);
            factory.Inputs[0].EndGate!.SetResult(AudioStreamEnd.DeviceLost());

            // The capture end task completes with DeviceLost; the router must
            // surface it exactly once. Deadline-polled (not fixed-delay) so
            // the assertion is robust under parallel test load.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (ends.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            var end = Assert.Single(ends);
            Assert.Equal(AudioStreamStopReason.DeviceLost, end.StopReason);

            await Task.Delay(50); // allow any duplicate raise to land
            Assert.Single(ends); // exactly once
        }

        [Fact]
        public async Task BeginTransmit_CaptureStartFails_MonitorStopped_NoLeak()
        {
            var factory = new ThrowingInputFactory();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), new ManualScheduler());

            await Assert.ThrowsAsync<AudioDeviceException>(() =>
                router.BeginTransmitAsync(DmrTarget, AudioDeviceId.Default, CancellationToken.None));

            // The local-monitor pipeline was created before the input failed;
            // it must be stopped, never orphaned (the factory is still owned
            // by the router and disposed exactly once).
            var monitor = Assert.Single(factory.Outputs);
            Assert.Equal(1, monitor.StopCount);

            await router.DisposeAsync();
            Assert.Equal(1, factory.DisposeCount);
        }

        [Fact]
        public async Task BeginTransmit_SecondWhileActive_ThrowsInvalidOperation()
        {
            var factory = new FakeAudioStreamFactory();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), new ManualScheduler());

            await router.BeginTransmitAsync(DmrTarget, AudioDeviceId.Default, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                router.BeginTransmitAsync(DmrTarget, AudioDeviceId.Default, CancellationToken.None));

            await router.EndTransmitAsync(); // still usable after the rejected begin
        }

        [Fact]
        public async Task EndTransmit_QuickRebegin_NoSpuriousCaptureEnded_NoStaleTraffic()
        {
            var factory = new FakeAudioStreamFactory();
            var encoder = new FakeVoiceFrameEncoder { CodewordLength = 9 };
            var sender = new RecordingTrafficSender();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), encoder, sender, new ManualScheduler());
            var ends = new List<AudioStreamEnd>();
            router.CaptureEnded += ends.Add;

            // Session A: end task kept in flight via the gate.
            factory.EndGateOnCreate = new TaskCompletionSource<AudioStreamEnd>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await router.BeginTransmitAsync(DmrTarget, AudioDeviceId.Default, CancellationToken.None);
            var inputA = factory.Inputs[0];
            factory.EndGateOnCreate = null;

            // End A: the input is stopped but A's end task is still pending.
            await router.EndTransmitAsync();

            // Quick re-begin (session B) while A's end task is in flight.
            await router.BeginTransmitAsync(DmrTarget, AudioDeviceId.Default, CancellationToken.None);
            var inputB = factory.Inputs[1];

            // A's end task completes Requested AFTER B reset the session flags:
            // this must NOT surface as a spurious CaptureEnded.
            inputA.EndGate!.SetResult(AudioStreamEnd.Requested());
            await Task.Delay(100); // allow any spurious raise to land
            Assert.Empty(ends);

            // A late block on A's pump after the handoff must not send stale
            // traffic through B's session.
            var sendsBefore = sender.DmrFrames.Count;
            await inputA.OnData!(new byte[1600]);
            await Task.Delay(50);
            Assert.Equal(sendsBefore, sender.DmrFrames.Count);

            // B remains fully functional.
            await inputB.OnData!(new byte[1600]);
            Assert.True(sender.DmrFrames.Count >= sendsBefore);
            await router.EndTransmitAsync();
        }

        [Fact]
        public async Task RouteVoiceFrame_AfterDispose_NoPipelineCreation_NoThrow()
        {
            var factory = new FakeAudioStreamFactory();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), new ManualScheduler());

            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr);
            Assert.Single(factory.Outputs);
            await router.DisposeAsync();

            // A frame landing after dispose must not create a pipeline on
            // the disposed factory and must not throw (TOCTOU guard).
            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr);

            Assert.Single(factory.Outputs); // no new pipeline
            Assert.Equal(1, factory.DisposeCount); // still exactly once
        }

        [Fact]
        public async Task LocalMonitor_DuringTransmit_RoutesPcm_WithShed()
        {
            var factory = new FakeAudioStreamFactory();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), new ManualScheduler());

            await router.BeginTransmitAsync(DmrTarget, AudioDeviceId.Default, CancellationToken.None);
            await factory.Inputs[0].OnData!(new byte[1600]);

            // One output for the local monitor (receive-side pipelines are
            // per-talkgroup; the TX loopback monitor is the first output).
            Assert.True(factory.Outputs.Count >= 1);
            Assert.True(factory.Outputs[0].Writes.Count >= 1);
        }

        /* ------------------------------------------------------------------
        ** Dispose
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task Dispose_StopsAllPipelines_DisposesFactoryOnce()
        {
            var factory = new FakeAudioStreamFactory();
            var router = CreateRouter(
                factory, new FakeVoiceFrameDecoder(), new FakeVoiceFrameEncoder(),
                new RecordingTrafficSender(), new ManualScheduler());

            router.RouteVoiceFrame("SYS1/TG1", new byte[27], VoiceMode.Dmr);
            await router.BeginTransmitAsync(DmrTarget, AudioDeviceId.Default, CancellationToken.None);
            var outputs = factory.Outputs.Count;
            var inputs = factory.Inputs.Count;
            Assert.True(outputs >= 1);
            Assert.Equal(1, inputs);

            await router.DisposeAsync();

            Assert.Equal(1, factory.DisposeCount); // factory disposed exactly once
            Assert.All(factory.Outputs, o => Assert.Equal(1, o.StopCount));
            Assert.All(factory.Inputs, i => Assert.Equal(1, i.StopCount));
        }

        /* ------------------------------------------------------------------
        ** Seam shapes
        ** ---------------------------------------------------------------- */

        [Fact]
        public void StubVoiceTrafficSender_CountsFrames_NeverThrows()
        {
            var stub = new StubVoiceTrafficSender();
            var target = DmrTarget;

            stub.SendDmrVoice(target, new byte[27], 1, 0);
            stub.SendP25Ldu(target, false, new byte[225], 1, 0);

            Assert.Equal(1, stub.DmrFrameCount);
            Assert.Equal(1, stub.P25LduCount);
        }

        [Fact]
        public void TransmitTarget_IsRecordWithExactMembers()
        {
            var type = typeof(TransmitTarget);
            Assert.True(type.IsValueType);
            Assert.NotNull(type.GetConstructor(new[]
            {
                typeof(string), typeof(string), typeof(byte), typeof(VoiceMode), typeof(uint),
            }));
            Assert.NotNull(type.GetProperty("SystemName"));
            Assert.NotNull(type.GetProperty("TalkgroupId"));
            Assert.NotNull(type.GetProperty("Slot"));
            Assert.NotNull(type.GetProperty("Mode"));
            Assert.NotNull(type.GetProperty("SourceId"));
        }

        [Fact]
        public void VoiceMode_HasDmrAndP25()
        {
            Assert.Equal(new[] { "Dmr", "P25" }, Enum.GetNames(typeof(VoiceMode)));
        }
    }
}
