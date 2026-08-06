// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the receive-side monitor audio pipeline slice
* (plan Task 9 / vertical-slice gate item 6: play local monitor audio):
*
*   DvmConsole.Platform.Audio.MonitorAudioPipeline
*   DvmConsole.Platform.Audio.IVoiceFrameDecoder
*
* The pipeline owns one IAudioOutput created through an injected
* IAudioStreamFactory at Start (AudioPcm.Console format), forwards PCM
* writes to it, sheds the oldest backlog when buffered audio exceeds a
* bounded duration (MacAudioBufferPolicy semantics: shed oldest, keep
* newest), optionally decodes 20 ms voice frames into 320-byte PCM via
* an injected IVoiceFrameDecoder seam, clamps and forwards Volume, and
* surfaces device loss through a single StreamEnded event (from
* Write() status; caller marshals to UI — pipeline is Dispatcher-free).
*
* Lifecycle locked here: Start once (second Start throws
* InvalidOperationException, parity MacAudioInput single-start),
* Start propagates AudioDeviceException(DeviceUnavailable) when the
* device is missing, StopAsync idempotent, writes after stop ->
* NotStarted, DisposeAsync idempotent and disposes the output and
* factory, StreamEnded raised exactly once per DeviceLost.
*/
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="MonitorAudioPipeline"/> and
    /// <see cref="IVoiceFrameDecoder"/>.
    /// </summary>
    public sealed class MonitorAudioPipelineTests
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

            /// <summary>
            /// When true, the write that follows the next ClearBuffer reports
            /// DeviceLost (models the device vanishing during a shed).
            /// </summary>
            public bool FailAfterClear;
            private bool failNextWrite;

            public AudioWriteResult Write(ReadOnlyMemory<byte> data)
            {
                if (failNextWrite)
                {
                    failNextWrite = false;
                    return new AudioWriteResult(AudioWriteStatus.DeviceLost, BufferedBytes);
                }

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
                if (FailAfterClear)
                {
                    failNextWrite = true;
                }
            }

            public Task StopAsync()
            {
                StopCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeAudioStreamFactory : IAudioStreamFactory
        {
            public FakeAudioOutput? Output;
            public AudioDeviceException? ThrowOnCreateOutput;
            public int DisposeCount { get; private set; }

            public IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format)
                => throw new NotSupportedException();

            public IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format)
            {
                if (ThrowOnCreateOutput is { } ex)
                {
                    throw ex;
                }

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

        /* ------------------------------------------------------------------
        ** Start / device resolution
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Start_CreatesOutputWithRequestedDeviceAndConsoleFormat()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new MonitorAudioPipeline(factory);
            var deviceId = AudioDeviceId.FromKey("output-1");

            pipeline.Start(deviceId);

            Assert.NotNull(factory.Output);
            Assert.Equal(deviceId, factory.Output!.Device.Id);
            Assert.Equal(AudioPcm.Console, factory.Output.Format);
            Assert.True(pipeline.IsRunning);
        }

        [Fact]
        public void Start_MissingDevice_PropagatesTypedException()
        {
            var factory = new FakeAudioStreamFactory
            {
                ThrowOnCreateOutput = new AudioDeviceException(
                    AudioDeviceErrorKind.DeviceUnavailable, "no device"),
            };
            var pipeline = new MonitorAudioPipeline(factory);

            var ex = Assert.Throws<AudioDeviceException>(() => pipeline.Start(AudioDeviceId.Default));
            Assert.Equal(AudioDeviceErrorKind.DeviceUnavailable, ex.Kind);
            Assert.False(pipeline.IsRunning);
        }

        [Fact]
        public void Start_Twice_Throws()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new MonitorAudioPipeline(factory);
            pipeline.Start(AudioDeviceId.Default);

            Assert.Throws<InvalidOperationException>(() => pipeline.Start(AudioDeviceId.Default));
        }

        /* ------------------------------------------------------------------
        ** PCM writes / backlog shed
        ** ---------------------------------------------------------------- */

        [Fact]
        public void WritePcm_ForwardsAndTracksBufferedBytes()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new MonitorAudioPipeline(factory);
            pipeline.Start(AudioDeviceId.Default);

            var pcm = new byte[320];
            var result = pipeline.WritePcm(pcm);

            Assert.Equal(AudioWriteStatus.Accepted, result.Status);
            Assert.Equal(320, result.BufferedBytes);
            Assert.Single(factory.Output!.Writes);
            Assert.Equal(pcm, factory.Output.Writes[0]);
        }

        [Fact]
        public void WritePcm_Overflow_ShedsOldestBacklogKeepsNewest()
        {
            var factory = new FakeAudioStreamFactory();
            // 50 ms shed threshold at the console format's BytesPerSecond
            // (16000) = 800 bytes: four 320-byte writes (1280 total) must
            // trigger at least one shed; the newest write is preserved.
            var pipeline = new MonitorAudioPipeline(factory, maxBufferedDuration: TimeSpan.FromMilliseconds(50));
            pipeline.Start(AudioDeviceId.Default);

            pipeline.WritePcm(new byte[320]);
            pipeline.WritePcm(new byte[320]);
            pipeline.WritePcm(new byte[320]);
            pipeline.WritePcm(new byte[320]);

            // Buffered bytes must never exceed the shed threshold; the
            // newest write is always preserved, and the shed path ran.
            Assert.True(factory.Output!.BufferedBytes <= 800);
            Assert.True(factory.Output.ClearCount >= 1);
            Assert.True(factory.Output.Writes.Count >= 1);
            Assert.True(factory.Output.Writes[^1].Length == 320);
        }

        [Fact]
        public void WritePcm_BelowThreshold_NoShed()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new MonitorAudioPipeline(factory, maxBufferedDuration: TimeSpan.FromMilliseconds(50));
            pipeline.Start(AudioDeviceId.Default);

            pipeline.WritePcm(new byte[320]);
            pipeline.WritePcm(new byte[320]); // 640 bytes < 800 threshold

            Assert.Equal(0, factory.Output!.ClearCount);
            Assert.Equal(2, factory.Output.Writes.Count);
            Assert.Equal(640, factory.Output.BufferedBytes);
        }

        [Fact]
        public void WritePcm_ShedPathDeviceLost_RaisesStreamEndedOnce()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new MonitorAudioPipeline(factory, maxBufferedDuration: TimeSpan.FromMilliseconds(50));
            var ends = new List<AudioStreamEnd>();
            pipeline.StreamEnded += ends.Add;
            pipeline.Start(AudioDeviceId.Default);
            factory.Output!.FailAfterClear = true;

            // Three 320-byte writes cross the 800-byte threshold on the
            // third write: the shed path clears the buffer and re-writes,
            // and the re-write reports DeviceLost (device vanished mid-shed).
            pipeline.WritePcm(new byte[320]);
            pipeline.WritePcm(new byte[320]);
            var result = pipeline.WritePcm(new byte[320]);

            Assert.Equal(AudioWriteStatus.DeviceLost, result.Status);
            var end = Assert.Single(ends);
            Assert.Equal(AudioStreamStopReason.DeviceLost, end.StopReason);

            // Subsequent writes report DeviceLost without re-raising.
            var again = pipeline.WritePcm(new byte[320]);
            Assert.Equal(AudioWriteStatus.DeviceLost, again.Status);
            Assert.Single(ends);
        }

        [Fact]
        public async Task StreamEnded_StaysSilentOnNormalStop()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new MonitorAudioPipeline(factory);
            var ends = new List<AudioStreamEnd>();
            pipeline.StreamEnded += ends.Add;
            pipeline.Start(AudioDeviceId.Default);

            pipeline.WritePcm(new byte[320]);
            await pipeline.StopAsync();

            Assert.Empty(ends);
        }

        /* ------------------------------------------------------------------
        ** Voice-frame decoding
        ** ---------------------------------------------------------------- */

        [Fact]
        public void WriteVoiceFrame_DecodesViaSeam_ExactlyOneFrameOfPcm()
        {
            var factory = new FakeAudioStreamFactory();
            var decoder = new FakeVoiceFrameDecoder();
            var pipeline = new MonitorAudioPipeline(factory, decoder);
            pipeline.Start(AudioDeviceId.Default);

            var result = pipeline.WriteVoiceFrame(new byte[27]); // DMR AMBE frame

            Assert.Equal(AudioWriteStatus.Accepted, result.Status);
            Assert.Equal(1, decoder.DecodeCount);
            Assert.Single(factory.Output!.Writes);
            Assert.Equal(320, factory.Output.Writes[0].Length);
        }

        [Fact]
        public void WriteVoiceFrame_WithoutDecoder_Throws()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new MonitorAudioPipeline(factory);
            pipeline.Start(AudioDeviceId.Default);

            Assert.Throws<InvalidOperationException>(() => pipeline.WriteVoiceFrame(new byte[27]));
        }

        [Fact]
        public void WriteVoiceFrame_DecoderRejects_SilentSkipNoCrash()
        {
            // WPF parity: a failed decode simply does not reach the
            // monitor stream (DMRDecodeAudioFrame skips on decode
            // failure). The pipeline stays healthy, nothing is written,
            // and the buffered byte count is unchanged.
            var factory = new FakeAudioStreamFactory();
            var decoder = new FakeVoiceFrameDecoder { Reject = true };
            var pipeline = new MonitorAudioPipeline(factory, decoder);
            pipeline.Start(AudioDeviceId.Default);

            var before = pipeline.WritePcm(new byte[320]).BufferedBytes;
            var result = pipeline.WriteVoiceFrame(new byte[27]);

            Assert.Equal(AudioWriteStatus.Accepted, result.Status);
            Assert.Equal(before, result.BufferedBytes);
            Assert.Single(factory.Output!.Writes); // only the explicit PCM write
            Assert.True(pipeline.IsRunning);
        }

        /* ------------------------------------------------------------------
        ** Volume / device loss / stop / dispose
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Volume_ClampsAndForwards()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new MonitorAudioPipeline(factory);
            pipeline.Start(AudioDeviceId.Default);

            pipeline.Volume = 1.5f;
            Assert.Equal(1f, factory.Output!.Volume);

            pipeline.Volume = -0.5f;
            Assert.Equal(0f, factory.Output.Volume);

            pipeline.Volume = 0.4f;
            Assert.Equal(0.4f, factory.Output.Volume);
        }

        [Fact]
        public void Write_DeviceLost_RaisesStreamEndedOnce()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new MonitorAudioPipeline(factory);
            var ends = new List<AudioStreamEnd>();
            pipeline.StreamEnded += ends.Add;
            pipeline.Start(AudioDeviceId.Default);
            factory.Output!.NextWriteStatus = AudioWriteStatus.DeviceLost;

            pipeline.WritePcm(new byte[320]);
            pipeline.WritePcm(new byte[320]);

            var end = Assert.Single(ends);
            Assert.Equal(AudioStreamStopReason.DeviceLost, end.StopReason);
        }

        [Fact]
        public async Task StopAsync_Idempotent_PostStopWritesReportNotStarted()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new MonitorAudioPipeline(factory);
            pipeline.Start(AudioDeviceId.Default);

            await pipeline.StopAsync();
            await pipeline.StopAsync();

            Assert.False(pipeline.IsRunning);
            Assert.Equal(2, factory.Output!.StopCount);
            var result = pipeline.WritePcm(new byte[320]);
            Assert.Equal(AudioWriteStatus.NotStarted, result.Status);
        }

        [Fact]
        public async Task DisposeAsync_Idempotent_DisposesOutputAndFactory()
        {
            var factory = new FakeAudioStreamFactory();
            var pipeline = new MonitorAudioPipeline(factory);
            pipeline.Start(AudioDeviceId.Default);

            await pipeline.DisposeAsync();
            await pipeline.DisposeAsync();

            Assert.Equal(1, factory.Output!.StopCount);
            Assert.Equal(1, factory.DisposeCount);
        }

        [Fact]
        public void Ctor_NullFactory_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new MonitorAudioPipeline(null!));
        }
    }
}
