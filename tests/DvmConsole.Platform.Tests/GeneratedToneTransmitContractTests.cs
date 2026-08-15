// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// RED contract gate for generated alert-tone PCM transport through the
    /// existing per-codeword encoder and traffic-sender seams.
    /// </summary>
    public sealed class GeneratedToneTransmitContractTests
    {
        private sealed class NoAudioFactory : IAudioStreamFactory
        {
            public int InputCreateCount { get; private set; }

            public IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format)
            {
                InputCreateCount++;
                throw new InvalidOperationException("Generated PCM must not open capture.");
            }

            public IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format)
                => throw new InvalidOperationException("Generated PCM has no monitor in this gate.");

            public IAudioFilePlayer CreateFilePlayer()
                => throw new InvalidOperationException("Generated PCM does not use file playback.");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class NoOpDecoder : IVoiceFrameDecoder
        {
            public bool TryDecode(ReadOnlyMemory<byte> voiceFrame, out short[] samples)
            {
                samples = Array.Empty<short>();
                return false;
            }
        }

        private sealed class RecordingEncoder : IVoiceFrameEncoder
        {
            private readonly Action<int>? onEncode;

            public RecordingEncoder(Action<int>? onEncode = null)
            {
                this.onEncode = onEncode;
            }

            public int EncodeCount { get; private set; }

            public bool TryEncode(
                VoiceMode mode,
                ReadOnlyMemory<short> samples,
                out byte[] codeword)
            {
                EncodeCount++;
                onEncode?.Invoke(EncodeCount);
                codeword = new byte[mode == VoiceMode.Dmr ? 9 : 11];
                codeword[0] = (byte)EncodeCount;
                return true;
            }
        }

        private sealed class RecordingSender : IVoiceTrafficSender
        {
            public readonly List<string> SignalOrder = new();
            public readonly List<(TransmitTarget Target, byte[] Frame, uint StreamId, int Seq)> DmrFrames = new();
            public readonly List<(TransmitTarget Target, bool GrantDemand, uint StreamId)> P25Tdus = new();
            public readonly List<(TransmitTarget Target, uint StreamId, int NextSeq)> DmrTerminators = new();
            public readonly List<(TransmitTarget Target, bool IsLdu2, byte[] Ldu, uint StreamId, int Seq)> P25Ldus = new();

            public void SendDmrVoice(
                TransmitTarget target,
                ReadOnlyMemory<byte> ambe27,
                uint streamId,
                int seqNo)
                => DmrFrames.Add((target, ambe27.ToArray(), streamId, seqNo));

            public void SendP25Ldu(
                TransmitTarget target,
                bool isLdu2,
                ReadOnlyMemory<byte> ldu225,
                uint streamId,
                int seqNo)
            {
                SignalOrder.Add("P25LDU");
                P25Ldus.Add((target, isLdu2, ldu225.ToArray(), streamId, seqNo));
            }

            public void SendDmrTerminator(TransmitTarget target, uint streamId, int nextSeqNo)
                => DmrTerminators.Add((target, streamId, nextSeqNo));

            public void SendP25Tdu(TransmitTarget target, uint streamId, bool grantDemand)
            {
                SignalOrder.Add($"P25TDU:{grantDemand}");
                P25Tdus.Add((target, grantDemand, streamId));
            }
        }

        private static readonly TransmitTarget DmrTarget = new(
            "SYS1", "31001", 1, VoiceMode.Dmr, 1001);

        private static readonly TransmitTarget P25Target = new(
            "SYS2", "2001", 0, VoiceMode.P25, 2001);

        private static TalkgroupAudioRouter CreateRouter(
            NoAudioFactory factory,
            RecordingEncoder encoder,
            RecordingSender sender)
            => new(
                factory,
                new NoOpDecoder(),
                encoder,
                sender,
                () => AudioDeviceId.Default);

        [Fact]
        public async Task TransmitPcmAsync_DmrFrames_EncodesAndSendsTerminator()
        {
            var factory = new NoAudioFactory();
            var encoder = new RecordingEncoder();
            var sender = new RecordingSender();
            await using var router = CreateRouter(factory, encoder, sender);

            await router.TransmitPcmAsync(
                new[] { DmrTarget },
                new byte[AudioPcm.FrameBytes * 3],
                sendStartSignal: false,
                CancellationToken.None);

            var frame = Assert.Single(sender.DmrFrames);
            Assert.Equal(DmrTarget, frame.Target);
            Assert.Equal(27, frame.Frame.Length);
            Assert.Equal(1u, frame.StreamId);
            Assert.Equal(0, frame.Seq);
            var terminator = Assert.Single(sender.DmrTerminators);
            Assert.Equal(DmrTarget, terminator.Target);
            Assert.Equal(1u, terminator.StreamId);
            Assert.Equal(1, terminator.NextSeq);
            Assert.Equal(3, encoder.EncodeCount);
            Assert.Equal(0, factory.InputCreateCount);
        }

        [Fact]
        public async Task TransmitPcmAsync_P25Frames_SendsGrantDemandAndEndTdus()
        {
            var factory = new NoAudioFactory();
            var encoder = new RecordingEncoder();
            var sender = new RecordingSender();
            await using var router = CreateRouter(factory, encoder, sender);

            await router.TransmitPcmAsync(
                new[] { P25Target },
                new byte[AudioPcm.FrameBytes * 9],
                sendStartSignal: true,
                CancellationToken.None);

            var ldu = Assert.Single(sender.P25Ldus);
            Assert.Equal(P25Target, ldu.Target);
            Assert.Equal(225, ldu.Ldu.Length);
            Assert.Equal(1u, ldu.StreamId);
            Assert.Equal(0, ldu.Seq);
            Assert.Equal(
                new[]
                {
                    "P25TDU:True",
                    "P25LDU",
                    "P25TDU:False",
                    "P25TDU:False",
                    "P25TDU:False",
                    "P25TDU:False",
                },
                sender.SignalOrder);
            Assert.Contains(sender.P25Tdus, t => t.GrantDemand && t.StreamId == 0);
            Assert.DoesNotContain(sender.P25Tdus, t => !t.GrantDemand && t.StreamId == 0);
            Assert.Equal(5, sender.P25Tdus.Count);
            Assert.Equal(9, encoder.EncodeCount);
            Assert.Equal(0, factory.InputCreateCount);
        }

        [Fact]
        public async Task TransmitPcmAsync_RejectsUnalignedPcm()
        {
            await using var router = CreateRouter(
                new NoAudioFactory(),
                new RecordingEncoder(),
                new RecordingSender());

            await Assert.ThrowsAsync<ArgumentException>(() => router.TransmitPcmAsync(
                new[] { DmrTarget },
                new byte[AudioPcm.FrameBytes - 1],
                sendStartSignal: false,
                CancellationToken.None));
        }

        [Fact]
        public async Task TransmitPcmAsync_Cancellation_EndsDmrExactlyOnce()
        {
            using var cancellation = new CancellationTokenSource();
            var factory = new NoAudioFactory();
            var encoder = new RecordingEncoder(count =>
            {
                if (count == 1)
                {
                    cancellation.Cancel();
                }
            });
            var sender = new RecordingSender();
            await using var router = CreateRouter(factory, encoder, sender);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.TransmitPcmAsync(
                new[] { DmrTarget },
                new byte[AudioPcm.FrameBytes * 6],
                sendStartSignal: false,
                cancellation.Token));

            Assert.Empty(sender.DmrFrames);
            var terminator = Assert.Single(sender.DmrTerminators);
            Assert.Equal(DmrTarget, terminator.Target);
            Assert.Equal(1u, terminator.StreamId);
            Assert.Equal(0, terminator.NextSeq);
        }

        [Fact]
        public async Task TransmitPcmAsync_NullTargets_ThrowsArgumentNullException()
        {
            await using var router = CreateRouter(
                new NoAudioFactory(),
                new RecordingEncoder(),
                new RecordingSender());

            await Assert.ThrowsAsync<ArgumentNullException>(() => router.TransmitPcmAsync(
                null!,
                new byte[AudioPcm.FrameBytes],
                sendStartSignal: false,
                CancellationToken.None));
        }

        [Fact]
        public async Task TransmitPcmAsync_EmptyTargetsOrPcm_IsNoOp()
        {
            var sender = new RecordingSender();
            await using var router = CreateRouter(
                new NoAudioFactory(),
                new RecordingEncoder(),
                sender);

            await router.TransmitPcmAsync(
                Array.Empty<TransmitTarget>(),
                new byte[AudioPcm.FrameBytes],
                sendStartSignal: true,
                CancellationToken.None);
            await router.TransmitPcmAsync(
                new[] { DmrTarget },
                ReadOnlyMemory<byte>.Empty,
                sendStartSignal: true,
                CancellationToken.None);

            Assert.Empty(sender.DmrFrames);
            Assert.Empty(sender.DmrTerminators);
            Assert.Empty(sender.P25Tdus);
        }

        [Fact]
        public async Task TransmitPcmAsync_DisposedNonEmpty_ThrowsObjectDisposedException()
        {
            var router = CreateRouter(
                new NoAudioFactory(),
                new RecordingEncoder(),
                new RecordingSender());
            await router.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => router.TransmitPcmAsync(
                new[] { DmrTarget },
                new byte[AudioPcm.FrameBytes],
                sendStartSignal: false,
                CancellationToken.None));
        }

        [Fact]
        public async Task TransmitPcmAsync_ActiveSession_ThrowsInvalidOperationException()
        {
            var started = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var router = CreateRouter(
                new NoAudioFactory(),
                new RecordingEncoder(count =>
                {
                    if (count == 1)
                    {
                        started.TrySetResult(true);
                    }
                }),
                new RecordingSender());

            try
            {
                var active = router.TransmitPcmAsync(
                    new[] { DmrTarget },
                    new byte[AudioPcm.FrameBytes * 10],
                    sendStartSignal: false,
                    cancellation.Token);
                await started.Task;

                await Assert.ThrowsAsync<InvalidOperationException>(() => router.TransmitPcmAsync(
                    new[] { DmrTarget },
                    new byte[AudioPcm.FrameBytes],
                    sendStartSignal: false,
                    CancellationToken.None));

                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            }
            finally
            {
                await router.DisposeAsync();
            }
        }
    }
}
