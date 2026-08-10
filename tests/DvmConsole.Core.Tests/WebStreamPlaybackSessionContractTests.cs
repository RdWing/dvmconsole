// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract for the decoder-independent web-stream PCM session.
    /// Network I/O, media decoding/resampling, and Platform audio ownership
    /// remain later seams.
    /// </summary>
    public sealed class WebStreamPlaybackSessionContractTests
    {
        [Fact]
        public void StartConnectPcmActivityAndStop_TracksSessionLifecycle()
        {
            DateTime now = new DateTime(2026, 8, 10, 15, 0, 0, DateTimeKind.Utc);
            var writes = new List<(uint StreamId, byte[] Pcm, double Volume)>();
            var session = new dvmconsole.WebStreamPlaybackSession(
                (streamId, pcm, volume) => writes.Add((streamId, pcm.ToArray(), volume)),
                () => now);

            session.Start();

            Assert.Equal("Connecting", session.StatusText);
            Assert.NotEqual((uint)0, session.StreamId);
            uint streamId = session.StreamId;

            session.MarkConnected();
            Assert.Equal("Idle", session.StatusText);

            session.Volume = 1.7;
            byte[] pcm = BuildPcm(900);
            session.AppendPcm(pcm);

            Assert.True(session.IsReceiving);
            Assert.Equal("RX", session.StatusText);
            var write = Assert.Single(writes);
            Assert.Equal(streamId, write.StreamId);
            Assert.Equal(pcm, write.Pcm);
            Assert.Equal(1.7, write.Volume, 3);

            now = now.AddMilliseconds(1400);
            session.Tick();
            Assert.True(session.IsReceiving);
            Assert.Equal("RX", session.StatusText);

            now = now.AddMilliseconds(1);
            session.AppendPcm(new byte[320]);
            Assert.False(session.IsReceiving);
            Assert.Equal("Idle", session.StatusText);

            session.AppendPcm(pcm);
            Assert.True(session.IsReceiving);
            now = now.AddMilliseconds(1401);
            session.Tick();
            Assert.False(session.IsReceiving);
            Assert.Equal("Idle", session.StatusText);

            session.Stop();
            Assert.Equal("Off", session.StatusText);
            Assert.Equal((uint)0, session.StreamId);
            Assert.False(session.IsReceiving);
            int writesAfterStop = writes.Count;
            session.AppendPcm(pcm);
            session.MarkRetry(2);
            Assert.Equal(writesAfterStop, writes.Count);
            Assert.Equal("Off", session.StatusText);
        }

        [Fact]
        public void Constructor_RejectsNullSink()
        {
            Assert.Throws<ArgumentNullException>(() => new dvmconsole.WebStreamPlaybackSession(null!));
        }

        [Fact]
        public void Volume_IsRoundedToTenthsAndClampedToWpfRange()
        {
            var session = new dvmconsole.WebStreamPlaybackSession((_, _, _) => { });

            session.Volume = -1;
            Assert.Equal(0.0, session.Volume);
            session.Volume = 1.26;
            Assert.Equal(1.3, session.Volume, 3);
            session.Volume = 4.6;
            Assert.Equal(4.0, session.Volume);
        }

        [Fact]
        public void SilentPcm_DoesNotEnterReceivingStateFromIdle()
        {
            var session = new dvmconsole.WebStreamPlaybackSession((_, _, _) => { });
            session.Start();
            session.MarkConnected();

            session.AppendPcm(new byte[320]);

            Assert.False(session.IsReceiving);
            Assert.Equal("Idle", session.StatusText);
        }

        [Fact]
        public void SilentPcm_DuringActivityHold_PreservesReceivingState()
        {
            DateTime now = new DateTime(2026, 8, 10, 15, 5, 0, DateTimeKind.Utc);
            var session = new dvmconsole.WebStreamPlaybackSession((_, _, _) => { }, () => now);
            session.Start();
            session.MarkConnected();
            session.AppendPcm(BuildPcm(900));

            now = now.AddMilliseconds(500);
            session.AppendPcm(new byte[320]);

            Assert.True(session.IsReceiving);
            Assert.Equal("RX", session.StatusText);
        }

        [Fact]
        public void ActivityDetection_UsesPeakOrRmsLegs()
        {
            var rmsSession = new dvmconsole.WebStreamPlaybackSession((_, _, _) => { });
            rmsSession.Start();
            rmsSession.MarkConnected();
            rmsSession.AppendPcm(BuildPcm(120));
            Assert.True(rmsSession.IsReceiving);

            var peakSession = new dvmconsole.WebStreamPlaybackSession((_, _, _) => { });
            peakSession.Start();
            peakSession.MarkConnected();
            peakSession.AppendPcm(BuildSparsePcm(1000));
            Assert.True(peakSession.IsReceiving);

            var thresholdSession = new dvmconsole.WebStreamPlaybackSession((_, _, _) => { });
            thresholdSession.Start();
            thresholdSession.MarkConnected();
            thresholdSession.AppendPcm(BuildSparsePcm(650));
            Assert.True(thresholdSession.IsReceiving);
        }

        [Fact]
        public void RetryAndFailureStatuses_AreExplicitAndStopResetsState()
        {
            var session = new dvmconsole.WebStreamPlaybackSession((_, _, _) => { });
            session.Start();

            session.MarkRetry(2);
            Assert.Equal("Retry 2/3", session.StatusText);
            session.MarkConnected();
            session.AppendPcm(BuildPcm(900));
            session.MarkFailed();
            Assert.Equal("Down", session.StatusText);
            Assert.False(session.IsReceiving);

            session.Stop();
            Assert.Equal("Off", session.StatusText);
        }

        [Fact]
        public void StartWhileActive_DoesNotReplaceTheOwnedStream()
        {
            var session = new dvmconsole.WebStreamPlaybackSession((_, _, _) => { });
            session.Start();
            uint streamId = session.StreamId;

            session.Start();

            Assert.Equal(streamId, session.StreamId);
            Assert.Equal("Connecting", session.StatusText);
        }

        private static byte[] BuildPcm(short sample)
        {
            var pcm = new byte[320];
            for (var i = 0; i < pcm.Length; i += 2)
            {
                pcm[i] = (byte)(sample & 0xFF);
                pcm[i + 1] = (byte)(sample >> 8);
            }

            return pcm;
        }

        private static byte[] BuildSparsePcm(short sample)
        {
            var pcm = new byte[320];
            pcm[0] = (byte)(sample & 0xFF);
            pcm[1] = (byte)(sample >> 8);
            return pcm;
        }
    }
}
