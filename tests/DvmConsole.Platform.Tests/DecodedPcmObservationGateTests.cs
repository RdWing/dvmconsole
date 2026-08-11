// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// RED contract for the decoded-PCM observation boundary.
    /// Observation is after one successful per-codeword decode, before the
    /// same PCM is optionally written to the local monitor. It must remain
    /// independent of monitor selection and must not alter routing.
    /// </summary>
    public sealed class DecodedPcmObservationGateTests
    {
        private sealed record Observation(string Key, VoiceMode Mode, byte[] Pcm);

        private sealed class RecordingObserver : IDecodedPcmObserver
        {
            public List<Observation> Observations { get; } = new();
            public bool ThrowOnObserve { get; set; }
            public int CallbackCount { get; private set; }

            public void ObserveDecodedPcm(
                string talkgroupKey,
                VoiceMode mode,
                ReadOnlyMemory<byte> pcm)
            {
                CallbackCount++;
                if (ThrowOnObserve)
                {
                    throw new InvalidOperationException("observer failure");
                }

                Observations.Add(new Observation(talkgroupKey, mode, pcm.ToArray()));
            }
        }

        private sealed class SequenceDecoder : IVoiceFrameDecoder
        {
            private readonly HashSet<int> rejectedCalls = new();

            public int DecodeCount { get; private set; }

            public void RejectCall(int callNumber) => rejectedCalls.Add(callNumber);

            public bool TryDecode(ReadOnlyMemory<byte> voiceFrame, out short[] samples)
            {
                DecodeCount++;
                if (rejectedCalls.Contains(DecodeCount))
                {
                    samples = Array.Empty<short>();
                    return false;
                }

                samples = new short[160];
                samples[0] = (short)DecodeCount;
                return true;
            }
        }

        private sealed class RecordingOutput : IAudioOutput
        {
            public RecordingOutput(AudioDeviceId deviceId, PcmFormat format)
            {
                Device = new AudioDeviceInfo(deviceId, AudioDeviceDirection.Output, "Recording output");
                Format = format;
            }

            public AudioDeviceInfo Device { get; }
            public PcmFormat Format { get; }
            public float Volume { get; set; }
            public List<byte[]> Writes { get; } = new();
            public int BufferedBytes { get; private set; }

            public AudioWriteResult Write(ReadOnlyMemory<byte> data)
            {
                Writes.Add(data.ToArray());
                BufferedBytes += data.Length;
                return new AudioWriteResult(AudioWriteStatus.Accepted, BufferedBytes);
            }

            public void ClearBuffer() => BufferedBytes = 0;

            public Task StopAsync() => Task.CompletedTask;
        }

        private sealed class RecordingFactory : IAudioStreamFactory
        {
            public bool FailOutput { get; set; }
            public List<RecordingOutput> Outputs { get; } = new();

            public IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format)
                => throw new NotSupportedException();

            public IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format)
            {
                if (FailOutput)
                {
                    throw new AudioDeviceException(
                        AudioDeviceErrorKind.DeviceUnavailable,
                        "monitor unavailable");
                }

                var output = new RecordingOutput(deviceId, format);
                Outputs.Add(output);
                return output;
            }

            public IAudioFilePlayer CreateFilePlayer() => throw new NotSupportedException();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class NoOpEncoder : IVoiceFrameEncoder
        {
            public bool TryEncode(VoiceMode mode, ReadOnlyMemory<short> samples, out byte[] voiceFrame)
            {
                voiceFrame = Array.Empty<byte>();
                return false;
            }
        }

        private sealed class NoOpSender : IVoiceTrafficSender
        {
            public void SendDmrVoice(TransmitTarget target, ReadOnlyMemory<byte> frame, uint streamId, int sequence) { }
            public void SendDmrTerminator(TransmitTarget target, uint streamId, int sequence) { }
            public void SendP25Ldu(TransmitTarget target, bool isLdu2, ReadOnlyMemory<byte> ldu, uint streamId, int sequence) { }
            public void SendP25Tdu(TransmitTarget target, uint streamId, bool grantDemand) { }
        }

        private static TalkgroupAudioRouter CreateRouter(
            RecordingFactory factory,
            SequenceDecoder decoder,
            IDecodedPcmObserver? observer = null)
            => new(
                factory,
                decoder,
                new NoOpEncoder(),
                new NoOpSender(),
                () => AudioDeviceId.Default,
                decodedPcmObserver: observer);

        private static byte[] DmrFrame() => new byte[27];

        private static byte[] P25Ldu() => new byte[225];

        [Fact]
        public async Task DmrAndP25_ObserveDecodedPcmInCodewordOrder()
        {
            var factory = new RecordingFactory();
            var decoder = new SequenceDecoder();
            var observer = new RecordingObserver();
            var router = CreateRouter(factory, decoder, observer);

            try
            {
                router.RouteVoiceFrame("SYS|DMR|slot:1", DmrFrame(), VoiceMode.Dmr);
                router.RouteVoiceFrame("SYS|P25", P25Ldu(), VoiceMode.P25);

                Assert.Equal(12, observer.Observations.Count);
                Assert.Equal(12, decoder.DecodeCount);
                Assert.Equal(12, factory.Outputs[0].Writes.Count + factory.Outputs[1].Writes.Count);

                for (var i = 0; i < 3; i++)
                {
                    Assert.Equal("SYS|DMR|slot:1", observer.Observations[i].Key);
                    Assert.Equal(VoiceMode.Dmr, observer.Observations[i].Mode);
                    Assert.Equal(i + 1, BinaryPrimitives.ReadInt16LittleEndian(observer.Observations[i].Pcm));
                    Assert.Equal(320, observer.Observations[i].Pcm.Length);
                }

                for (var i = 3; i < 12; i++)
                {
                    Assert.Equal("SYS|P25", observer.Observations[i].Key);
                    Assert.Equal(VoiceMode.P25, observer.Observations[i].Mode);
                    Assert.Equal(i + 1, BinaryPrimitives.ReadInt16LittleEndian(observer.Observations[i].Pcm));
                    Assert.Equal(320, observer.Observations[i].Pcm.Length);
                }
            }
            finally
            {
                await router.DisposeAsync();
            }
        }

        [Fact]
        public async Task FailedDecode_EmitsNothing_AndDoesNotDecodeTwice()
        {
            var factory = new RecordingFactory();
            var decoder = new SequenceDecoder();
            decoder.RejectCall(2);
            var observer = new RecordingObserver();
            var router = CreateRouter(factory, decoder, observer);

            try
            {
                router.RouteVoiceFrame("SYS|DMR|slot:1", DmrFrame(), VoiceMode.Dmr);

                Assert.Equal(3, decoder.DecodeCount);
                Assert.Equal(2, observer.Observations.Count);
                Assert.Equal(2, factory.Outputs[0].Writes.Count);
                Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(observer.Observations[0].Pcm));
                Assert.Equal(3, BinaryPrimitives.ReadInt16LittleEndian(observer.Observations[1].Pcm));
            }
            finally
            {
                await router.DisposeAsync();
            }
        }

        [Fact]
        public async Task UnavailableMonitor_DoesNotGateObservation()
        {
            var factory = new RecordingFactory { FailOutput = true };
            var decoder = new SequenceDecoder();
            var observer = new RecordingObserver();
            var router = CreateRouter(factory, decoder, observer);

            try
            {
                router.RouteVoiceFrame("SYS|DMR|slot:1", DmrFrame(), VoiceMode.Dmr);

                Assert.Equal(3, decoder.DecodeCount);
                Assert.Equal(3, observer.Observations.Count);
                Assert.Empty(factory.Outputs);
            }
            finally
            {
                await router.DisposeAsync();
            }
        }

        [Fact]
        public async Task ObserverException_DoesNotKillRouting_OrCauseSecondDecode()
        {
            var factory = new RecordingFactory();
            var decoder = new SequenceDecoder();
            var observer = new RecordingObserver { ThrowOnObserve = true };
            var router = CreateRouter(factory, decoder, observer);

            try
            {
                var exception = Record.Exception(
                    () => router.RouteVoiceFrame("SYS|DMR|slot:1", DmrFrame(), VoiceMode.Dmr));

                Assert.Null(exception);
                Assert.Equal(3, observer.CallbackCount);
                Assert.Equal(3, decoder.DecodeCount);
                Assert.Equal(3, factory.Outputs[0].Writes.Count);
            }
            finally
            {
                await router.DisposeAsync();
            }
        }

        [Fact]
        public async Task Dispose_StopsObservation()
        {
            var factory = new RecordingFactory();
            var decoder = new SequenceDecoder();
            var observer = new RecordingObserver();
            var router = CreateRouter(factory, decoder, observer);

            router.RouteVoiceFrame("SYS|DMR|slot:1", DmrFrame(), VoiceMode.Dmr);
            await router.DisposeAsync();
            router.RouteVoiceFrame("SYS|DMR|slot:1", DmrFrame(), VoiceMode.Dmr);

            Assert.Equal(3, observer.Observations.Count);
            Assert.Equal(3, decoder.DecodeCount);
        }
    }
}
