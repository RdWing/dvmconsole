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
    /// RED contracts for Gate 3.3's local permit-tone playback and RX-only
    /// speaker suppression. Observer/TAR and traffic seams must remain alive.
    /// </summary>
    public sealed class TalkgroupAudioRouterPreferencesContractTests
    {
        private sealed class FakeOutput : IAudioOutput
        {
            public FakeOutput(AudioDeviceId deviceId, PcmFormat format)
            {
                Device = new AudioDeviceInfo(deviceId, AudioDeviceDirection.Output, "Fake Output");
                Format = format;
            }

            public AudioDeviceInfo Device { get; }
            public PcmFormat Format { get; }
            public float Volume { get; set; }
            public List<byte[]> Writes { get; } = new();
            public int ClearBufferCount { get; private set; }
            public int StopCount { get; private set; }

            public AudioWriteResult Write(ReadOnlyMemory<byte> data)
            {
                Writes.Add(data.ToArray());
                return new AudioWriteResult(AudioWriteStatus.Accepted, data.Length);
            }

            public void ClearBuffer()
            {
                ClearBufferCount++;
            }

            public Task StopAsync()
            {
                StopCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeInput : IAudioInput
        {
            public FakeInput(AudioDeviceId deviceId, PcmFormat format)
            {
                Device = new AudioDeviceInfo(deviceId, AudioDeviceDirection.Input, "Fake Input");
                Format = format;
            }

            public AudioDeviceInfo Device { get; }
            public PcmFormat Format { get; }

            public Task<AudioStreamEnd> StartAsync(
                Func<ReadOnlyMemory<byte>, Task> onData,
                CancellationToken cancellationToken)
                => Task.FromResult(AudioStreamEnd.Requested());

            public Task StopAsync() => Task.CompletedTask;
        }

        private sealed class FakeFactory : IAudioStreamFactory
        {
            public List<FakeOutput> Outputs { get; } = new();
            public int InputCreateCount { get; private set; }
            public int DisposeCount { get; private set; }

            public IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format)
            {
                InputCreateCount++;
                return new FakeInput(deviceId, format);
            }

            public IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format)
            {
                var output = new FakeOutput(deviceId, format);
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

        private sealed class Decoder : IVoiceFrameDecoder
        {
            public bool TryDecode(ReadOnlyMemory<byte> voiceFrame, out short[] samples)
            {
                samples = new short[160];
                return true;
            }
        }

        private sealed class Encoder : IVoiceFrameEncoder
        {
            public bool TryEncode(
                VoiceMode mode,
                ReadOnlyMemory<short> samples,
                out byte[] codeword)
            {
                codeword = new byte[mode == VoiceMode.Dmr ? 9 : 11];
                return true;
            }
        }

        private sealed class Sender : IVoiceTrafficSender
        {
            public int VoiceCount { get; private set; }

            public void SendDmrVoice(TransmitTarget target, ReadOnlyMemory<byte> ambe27, uint streamId, int seqNo)
                => VoiceCount++;

            public void SendP25Ldu(TransmitTarget target, bool isLdu2, ReadOnlyMemory<byte> ldu225, uint streamId, int seqNo)
                => VoiceCount++;

            public void SendDmrTerminator(TransmitTarget target, uint streamId, int nextSeqNo)
            {
            }

            public void SendP25Tdu(TransmitTarget target, uint streamId, bool grantDemand)
            {
            }
        }

        private sealed class Observer : IDecodedPcmObserver
        {
            public int FrameCount { get; private set; }

            public void ObserveDecodedPcm(string talkgroupKey, VoiceMode mode, ReadOnlyMemory<byte> pcm)
                => FrameCount++;
        }

        private sealed class NoopSchedule : IDisposable
        {
            public void Dispose()
            {
            }
        }

        private static TalkgroupAudioRouter CreateRouter(
            FakeFactory factory,
            Observer observer,
            Func<bool> speakerEnabled,
            Sender? sender = null)
            => new(
                factory,
                new Decoder(),
                new Encoder(),
                sender ?? new Sender(),
                () => AudioDeviceId.Default,
                scheduler: (_, _) => new NoopSchedule(),
                decodedPcmObserver: observer,
                resolveSpeakerOutputEnabled: _ => speakerEnabled());

        [Fact]
        public async Task RxMute_SuppressesSpeakerWritesOnly_AndObserverContinues()
        {
            var factory = new FakeFactory();
            var observer = new Observer();
            var muted = true;
            await using var router = CreateRouter(factory, observer, () => !muted);

            router.RouteVoiceFrame("SYS/TG", new byte[27], VoiceMode.Dmr);
            var output = Assert.Single(factory.Outputs);
            Assert.Empty(output.Writes);
            Assert.Equal(3, observer.FrameCount);

            muted = false;
            router.RouteVoiceFrame("SYS/TG", new byte[27], VoiceMode.Dmr);
            Assert.Equal(3, output.Writes.Count);
            Assert.Equal(6, observer.FrameCount);
        }

        [Fact]
        public async Task ClearAllTalkgroupBuffers_ClearsSpeakerBacklogWithoutEndingRouterState()
        {
            var factory = new FakeFactory();
            var observer = new Observer();
            await using var router = CreateRouter(factory, observer, () => true);

            router.RouteVoiceFrame("SYS/TG", new byte[27], VoiceMode.Dmr);
            var output = Assert.Single(factory.Outputs);
            router.ClearAllTalkgroupBuffers();

            Assert.Equal(1, output.ClearBufferCount);
            Assert.Equal(3, observer.FrameCount);

            router.RouteVoiceFrame("SYS/TG", new byte[27], VoiceMode.Dmr);
            Assert.Equal(6, observer.FrameCount);
            Assert.Equal(6, output.Writes.Count);
        }

        [Fact]
        public async Task PlayLocalPcmAsync_WritesLocalOutputWithoutCaptureOrTraffic()
        {
            var factory = new FakeFactory();
            var observer = new Observer();
            var sender = new Sender();
            await using var router = CreateRouter(factory, observer, () => true, sender);

            await router.PlayLocalPcmAsync(new byte[800]);

            var output = Assert.Single(factory.Outputs);
            Assert.Single(output.Writes);
            Assert.Equal(800, output.Writes[0].Length);
            Assert.Equal(0, factory.InputCreateCount);
            Assert.Equal(0, sender.VoiceCount);
            Assert.Equal(1, output.StopCount);
        }
    }
}
