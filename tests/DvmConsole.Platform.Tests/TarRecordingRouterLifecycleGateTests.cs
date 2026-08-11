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
    /// Gate 1.4 RED contracts at the router boundary: TX PCM is emitted for
    /// every resolved target, observer failures are isolated, and RX idle
    /// closure still fires when monitor output cannot be created.
    /// </summary>
    public sealed class TarRecordingRouterLifecycleGateTests
    {
        [Fact]
        public async Task TransmitPcm_ObservesEveryResolvedTargetFrame()
        {
            var observer = new RecordingTransmitObserver();
            var targetA = new TransmitTarget("SYS", "101", 1, VoiceMode.Dmr, 9001);
            var targetB = new TransmitTarget("SYS", "202", 1, VoiceMode.Dmr, 9001);
            var router = CreateRouter(observer, new RecordingDecoder(), new RecordingEncoder(), new RecordingSender());

            await router.TransmitPcmAsync(
                new[] { targetA, targetB },
                new byte[AudioPcm.FrameBytes * 2],
                sendStartSignal: false,
                CancellationToken.None);

            Assert.Equal(4, observer.Frames.Count);
            Assert.Equal(2, observer.Frames.FindAll(frame => frame.Target.Equals(targetA)).Count);
            Assert.Equal(2, observer.Frames.FindAll(frame => frame.Target.Equals(targetB)).Count);
            await router.DisposeAsync();
        }

        [Fact]
        public async Task TransmitPcm_ObserverFailureDoesNotSuppressNetworkSend()
        {
            var observer = new RecordingTransmitObserver { Throw = true };
            var sender = new RecordingSender();
            var router = CreateRouter(observer, new RecordingDecoder(), new RecordingEncoder(), sender);

            await router.TransmitPcmAsync(
                new[] { new TransmitTarget("SYS", "101", 1, VoiceMode.Dmr, 9001) },
                new byte[AudioPcm.FrameBytes * 3],
                sendStartSignal: false,
                CancellationToken.None);

            Assert.Single(sender.DmrFrames);
            await router.DisposeAsync();
        }

        [Fact]
        public async Task ReceiveIdleWithoutMonitorOutput_RaisesOneStreamEndAndStillObservesPcm()
        {
            var scheduler = new ManualScheduler();
            var observer = new RecordingDecodedObserver();
            var ended = new List<(string Key, VoiceMode Mode)>();
            var router = new TalkgroupAudioRouter(
                new ThrowingOutputFactory(),
                new RecordingDecoder(),
                new RecordingEncoder(),
                new RecordingSender(),
                () => AudioDeviceId.Default,
                idleReleaseDelay: TimeSpan.FromSeconds(2),
                scheduler: scheduler.Schedule,
                decodedPcmObserver: observer);
            router.TalkgroupStreamEnded += (key, mode) => ended.Add((key, mode));

            router.RouteVoiceFrame("SYS|101", new byte[27], VoiceMode.Dmr);
            scheduler.FireAll();
            scheduler.FireAll();

            Assert.Equal(3, observer.Frames.Count);
            Assert.Single(ended);
            Assert.Equal("SYS|101", ended[0].Key);
            Assert.Equal(VoiceMode.Dmr, ended[0].Mode);
            await router.DisposeAsync();
        }

        private static TalkgroupAudioRouter CreateRouter(
            ITransmittedPcmObserver observer,
            IVoiceFrameDecoder decoder,
            IVoiceFrameEncoder encoder,
            IVoiceTrafficSender sender)
            => new TalkgroupAudioRouter(
                new NoopFactory(),
                decoder,
                encoder,
                sender,
                () => AudioDeviceId.Default,
                transmittedPcmObserver: observer);

        private sealed class RecordingTransmitObserver : ITransmittedPcmObserver
        {
            public readonly List<(TransmitTarget Target, byte[] Pcm)> Frames = new();
            public bool Throw { get; set; }

            public void ObserveTransmittedPcm(TransmitTarget target, ReadOnlyMemory<byte> pcm)
            {
                Frames.Add((target, pcm.ToArray()));
                if (Throw)
                    throw new InvalidOperationException("observer failure");
            }
        }

        private sealed class RecordingDecodedObserver : IDecodedPcmObserver
        {
            public readonly List<byte[]> Frames = new();

            public void ObserveDecodedPcm(string talkgroupKey, VoiceMode mode, ReadOnlyMemory<byte> pcm)
                => Frames.Add(pcm.ToArray());
        }

        private sealed class RecordingDecoder : IVoiceFrameDecoder
        {
            public bool TryDecode(ReadOnlyMemory<byte> voiceFrame, out short[] samples)
            {
                samples = new short[160];
                return true;
            }
        }

        private sealed class RecordingEncoder : IVoiceFrameEncoder
        {
            public bool TryEncode(VoiceMode mode, ReadOnlyMemory<short> samples, out byte[] codeword)
            {
                codeword = new byte[9];
                return true;
            }
        }

        private sealed class RecordingSender : IVoiceTrafficSender
        {
            public readonly List<byte[]> DmrFrames = new();

            public void SendDmrVoice(TransmitTarget target, ReadOnlyMemory<byte> ambe27, uint streamId, int seqNo)
                => DmrFrames.Add(ambe27.ToArray());

            public void SendP25Ldu(TransmitTarget target, bool isLdu2, ReadOnlyMemory<byte> ldu225, uint streamId, int seqNo)
            {
            }

            public void SendDmrTerminator(TransmitTarget target, uint streamId, int nextSeqNo)
            {
            }

            public void SendP25Tdu(TransmitTarget target, uint streamId, bool grantDemand)
            {
            }
        }

        private class NoopFactory : IAudioStreamFactory
        {
            public virtual IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format) => null!;
            public virtual IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format) => null!;
            public virtual IAudioFilePlayer CreateFilePlayer() => null!;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class ThrowingOutputFactory : NoopFactory
        {
            public override IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format)
                => throw new AudioDeviceException(AudioDeviceErrorKind.DeviceUnavailable, "output unavailable");
        }

        private sealed class ManualScheduler
        {
            private readonly List<Action?> actions = new();

            public IDisposable Schedule(TimeSpan delay, Action action)
            {
                actions.Add(action);
                var index = actions.Count - 1;
                return new Cancellation(() => actions[index] = null);
            }

            public void FireAll()
            {
                foreach (var action in actions.ToArray())
                    action?.Invoke();
            }

            private sealed class Cancellation : IDisposable
            {
                private readonly Action cancel;
                public Cancellation(Action cancel) => this.cancel = cancel;
                public void Dispose() => cancel();
            }
        }
    }
}
