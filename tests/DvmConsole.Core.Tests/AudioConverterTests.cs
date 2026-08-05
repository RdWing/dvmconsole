// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2025 Caleb, K4PHP
*
*/
/**
* Deterministic contract tests for the portable audio conversion surface
* (AudioConverter + AudioConverterLog) that must live in DvmConsole.Core:
* SplitToChunks/CombineChunks chunking semantics, PcmToFloat normalization
* and clamping, the exact invalid-input messages routed through the
* AudioConverterLog logging seam, and the seam's public API shape. The
* production surface is now Core-owned while WPF installs the logging route
* through App.xaml.cs. All assertions are
* deterministic: no wall-clock time, no log timestamps/internals, no
* external files, no secrets.
*/
using System.Reflection;
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Contract tests for the Core-owned audio conversion surface.
    /// </summary>
    public class AudioConverterTests
    {
        /*
        ** Ownership and public API shape
        */

        /// <summary>
        /// AudioConverter must compile into the DvmConsole.Core assembly so
        /// the WPF console and headless tooling share one portable
        /// definition. If it regresses to a linked compile in the WPF or
        /// test project, the extraction boundary is broken.
        /// </summary>
        [Fact]
        public void AudioConverter_OwnedByDvmConsoleCoreAssembly()
        {
            Assert.Equal("DvmConsole.Core", typeof(AudioConverter).Assembly.GetName().Name);
        }

        /// <summary>
        /// The PCM chunk-size constants are the public conversion surface and
        /// must keep their exact values: 1600-byte big chunks split into
        /// 320-byte small chunks.
        /// </summary>
        [Fact]
        public void AudioConverter_ExposesPcmLengthConstants()
        {
            Assert.Equal(1600, AudioConverter.OriginalPcmLength);
            Assert.Equal(320, AudioConverter.ExpectedPcmLength);
        }

        /// <summary>
        /// The logging seam exposes a public static get/set
        /// <see cref="Action{T}"/> route property.
        /// </summary>
        [Fact]
        public void AudioConverterLog_Route_IsPublicStaticActionString()
        {
            PropertyInfo route = typeof(AudioConverterLog).GetProperty(
                nameof(AudioConverterLog.Route),
                BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(route);
            Assert.True(route.CanRead && route.CanWrite);
            Assert.Equal(typeof(Action<string>), route.PropertyType);
        }

        /// <summary>
        /// The logging seam exposes a public static void WriteLine(string)
        /// used by the invalid-input paths.
        /// </summary>
        [Fact]
        public void AudioConverterLog_WriteLine_IsPublicStaticVoidStringMethod()
        {
            MethodInfo writeLine = typeof(AudioConverterLog).GetMethod(
                nameof(AudioConverterLog.WriteLine),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);
            Assert.NotNull(writeLine);
            Assert.Equal(typeof(void), writeLine.ReturnType);
        }

        /// <summary>
        /// The route defaults to null (no-op): WriteLine must not throw when
        /// no consumer has installed a route.
        /// </summary>
        [Fact]
        public void AudioConverterLog_Route_DefaultsToNullNoOp()
        {
            Assert.Null(AudioConverterLog.Route);
            AudioConverterLog.WriteLine("no-op default must not throw");
        }

        /// <summary>
        /// A routed message must reach the installed route. The seam may
        /// decorate the message (call-site prefix); only the payload and
        /// single-delivery semantics are pinned here. Exact full-message
        /// format is pinned by the invalid-input tests below.
        /// </summary>
        [Fact]
        public void AudioConverterLog_WriteLine_RoutesMessageToRoute()
        {
            using (RouteCapture capture = new RouteCapture())
            {
                AudioConverterLog.WriteLine("hello seam");
                Assert.Single(capture.Messages);
                Assert.EndsWith("hello seam", capture.Messages[0]);
            }
        }

        /*
        ** SplitToChunks
        */

        /// <summary>
        /// A valid 1600-byte buffer splits into exactly five 320-byte chunks
        /// whose bytes match the source segment-for-segment.
        /// </summary>
        [Fact]
        public void SplitToChunks_Valid1600Bytes_ReturnsFiveExact320ByteChunks()
        {
            byte[] pcm = BuildPattern(1600);
            List<byte[]> chunks = AudioConverter.SplitToChunks(pcm);

            Assert.Equal(5, chunks.Count);
            for (int c = 0; c < chunks.Count; c++)
            {
                Assert.Equal(320, chunks[c].Length);
                Assert.Equal(pcm.Skip(c * 320).Take(320), chunks[c]);
            }
        }

        /// <summary>
        /// SplitToChunks followed by CombineChunks must be a lossless
        /// round-trip for the default sizes.
        /// </summary>
        [Fact]
        public void SplitToChunks_RoundTripsThroughCombineChunks()
        {
            byte[] pcm = BuildPattern(1600);
            List<byte[]> chunks = AudioConverter.SplitToChunks(pcm);

            Assert.Equal(pcm, AudioConverter.CombineChunks(chunks));
        }

        /// <summary>
        /// A length that does not match origLen returns an empty list and
        /// routes the exact invalid-length message (including the reflection
        /// call-site prefix rendered by the logging seam).
        /// </summary>
        [Fact]
        public void SplitToChunks_WrongLength_ReturnsEmptyListAndRoutesExactMessage()
        {
            byte[] pcm = BuildPattern(1599);

            using (RouteCapture capture = new RouteCapture())
            {
                List<byte[]> chunks = AudioConverter.SplitToChunks(pcm);

                Assert.Empty(chunks);
                Assert.Equal(
                    "<AudioConverter::SplitToChunks(Byte[], Int32, Int32)> Invalid PCM length: 1599, expected: 1600",
                    Assert.Single(capture.Messages));
            }
        }

        /// <summary>
        /// A null buffer dereferences before any validation can run and must
        /// surface as a NullReferenceException.
        /// </summary>
        [Fact]
        public void SplitToChunks_NullInput_ThrowsNullReferenceException()
        {
            Assert.Throws<NullReferenceException>(() => AudioConverter.SplitToChunks(null));
        }

        /// <summary>
        /// With origLen=800 and a divisible expectedLength=160 the loop runs
        /// five times, but every chunk is still allocated at the fixed
        /// 320-byte ExpectedPcmLength with only the first 160 bytes copied
        /// and the remainder zero-filled. This preserves the existing
        /// allocation quirk exactly (a 160-byte expectedLength must not
        /// silently change chunk sizing downstream).
        /// </summary>
        [Fact]
        public void SplitToChunks_CustomDivisibleLength_Preserves320ByteAllocationQuirk()
        {
            byte[] pcm = BuildPattern(800);
            List<byte[]> chunks = AudioConverter.SplitToChunks(pcm, 800, 160);

            Assert.Equal(5, chunks.Count);
            for (int c = 0; c < chunks.Count; c++)
            {
                Assert.Equal(320, chunks[c].Length);
                Assert.Equal(pcm.Skip(c * 160).Take(160), chunks[c].Take(160));
                Assert.All(chunks[c].Skip(160), b => Assert.Equal((byte)0, b));
            }
        }

        /// <summary>
        /// A non-divisible expectedLength makes the final Buffer.BlockCopy
        /// overrun the source buffer and must surface as an
        /// ArgumentException from BlockCopy.
        /// </summary>
        [Fact]
        public void SplitToChunks_NonDivisibleCustomLength_ThrowsArgumentException()
        {
            byte[] pcm = BuildPattern(1600);

            Assert.Throws<ArgumentException>(() => AudioConverter.SplitToChunks(pcm, 1600, 300));
        }

        /*
        ** CombineChunks
        */

        /// <summary>
        /// Five 320-byte chunks combine back into the exact 1600-byte source
        /// buffer.
        /// </summary>
        [Fact]
        public void CombineChunks_ValidChunks_ReturnsOriginalBytes()
        {
            byte[] pcm = BuildPattern(1600);
            List<byte[]> chunks = AudioConverter.SplitToChunks(pcm);

            byte[] combined = AudioConverter.CombineChunks(chunks);

            Assert.Equal(1600, combined.Length);
            Assert.Equal(pcm, combined);
        }

        /// <summary>
        /// A chunk count whose total length does not match origLen returns
        /// null and routes the exact invalid-count message (including the
        /// reflection call-site prefix with the List`1 parameter name).
        /// </summary>
        [Fact]
        public void CombineChunks_CountMismatch_ReturnsNullAndRoutesExactMessage()
        {
            List<byte[]> chunks = AudioConverter.SplitToChunks(BuildPattern(1600)).Take(4).ToList();

            using (RouteCapture capture = new RouteCapture())
            {
                Assert.Null(AudioConverter.CombineChunks(chunks));
                Assert.Equal(
                    "<AudioConverter::CombineChunks(List`1, Int32, Int32)> Invalid number of chunks: 4, expected total length: 1600",
                    Assert.Single(capture.Messages));
            }
        }

        /// <summary>
        /// An empty chunk list with origLen=0 passes the count check and must
        /// yield an empty (non-null) byte array.
        /// </summary>
        [Fact]
        public void CombineChunks_EmptyListWithOrigLenZero_ReturnsEmptyArray()
        {
            byte[] combined = AudioConverter.CombineChunks(new List<byte[]>(), 0);

            Assert.NotNull(combined);
            Assert.Empty(combined);
        }

        /// <summary>
        /// A chunk shorter than expectedLength makes Buffer.BlockCopy overrun
        /// the source and must surface as an ArgumentException.
        /// </summary>
        [Fact]
        public void CombineChunks_ShortChunk_ThrowsArgumentException()
        {
            List<byte[]> chunks = AudioConverter.SplitToChunks(BuildPattern(1600));
            chunks[2] = BuildPattern(319);

            Assert.Throws<ArgumentException>(() => AudioConverter.CombineChunks(chunks));
        }

        /// <summary>
        /// A chunk longer than expectedLength must be truncated: only the
        /// leading 320 bytes are copied into the combined buffer and the
        /// excess tail must not leak into the output.
        /// </summary>
        [Fact]
        public void CombineChunks_LongChunk_TruncatesToExpectedLength()
        {
            List<byte[]> chunks = AudioConverter.SplitToChunks(BuildPattern(1600));
            byte[] oversized = new byte[640];
            for (int i = 0; i < oversized.Length; i++)
                oversized[i] = (byte)(i < 320 ? 0xAB : 0xCD);
            chunks[0] = oversized;

            byte[] combined = AudioConverter.CombineChunks(chunks);

            Assert.Equal(1600, combined.Length);
            Assert.All(combined.Take(320), b => Assert.Equal((byte)0xAB, b));
            Assert.Equal(BuildPattern(1600).Skip(320), combined.Skip(320));
        }

        /*
        ** PcmToFloat
        */

        /// <summary>
        /// An empty PCM buffer yields an empty float array.
        /// </summary>
        [Fact]
        public void PcmToFloat_Empty_ReturnsEmptyArray()
        {
            Assert.Empty(AudioConverter.PcmToFloat(Array.Empty<short>()));
        }

        /// <summary>
        /// Silence maps to exactly zero.
        /// </summary>
        [Fact]
        public void PcmToFloat_Zero_ReturnsZero()
        {
            Assert.Equal(0f, Assert.Single(AudioConverter.PcmToFloat(new short[] { 0 })));
        }

        /// <summary>
        /// short.MaxValue normalizes to exactly one.
        /// </summary>
        [Fact]
        public void PcmToFloat_ShortMaxValue_ReturnsOne()
        {
            Assert.Equal(1f, Assert.Single(AudioConverter.PcmToFloat(new short[] { short.MaxValue })));
        }

        /// <summary>
        /// short.MinValue exceeds -1 after division and must clamp to exactly
        /// minus one.
        /// </summary>
        [Fact]
        public void PcmToFloat_ShortMinValue_ClampsToMinusOne()
        {
            Assert.Equal(-1f, Assert.Single(AudioConverter.PcmToFloat(new short[] { short.MinValue })));
        }

        /// <summary>
        /// Ordinary positive and negative samples normalize to
        /// sample / short.MaxValue within a small tolerance.
        /// </summary>
        [Fact]
        public void PcmToFloat_OrdinaryValues_MatchNormalizedWithTolerance()
        {
            float[] floats = AudioConverter.PcmToFloat(new short[] { 1000, -1000, 16384, -16384 });

            Assert.Equal(4, floats.Length);
            Assert.Equal(0.0305, floats[0], 4);   //  1000 / 32767
            Assert.Equal(-0.0305, floats[1], 4);  // -1000 / 32767
            Assert.Equal(0.5, floats[2], 4);      //  16384 / 32767
            Assert.Equal(-0.5, floats[3], 4);     // -16384 / 32767
        }

        /// <summary>
        /// PcmToFloat must not mutate the caller's PCM buffer.
        /// </summary>
        [Fact]
        public void PcmToFloat_DoesNotMutateInput()
        {
            short[] input = { 1000, -1000, short.MaxValue, short.MinValue, 0 };
            short[] snapshot = (short[])input.Clone();

            AudioConverter.PcmToFloat(input);

            Assert.Equal(snapshot, input);
        }

        /*
        ** Helpers
        */

        /// <summary>
        /// Deterministic byte pattern ((i * 31 + 7) &amp; 0xFF): every index
        /// maps to a known byte with no RNG state; assertions compare each
        /// expected offset explicitly.
        /// </summary>
        private static byte[] BuildPattern(int length)
        {
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++)
                data[i] = (byte)((i * 31 + 7) & 0xFF);
            return data;
        }

        /// <summary>
        /// Temporarily installs a recording route on
        /// <see cref="AudioConverterLog.Route"/> and restores the previous
        /// route on dispose, so the static logging state can never leak
        /// between tests. Captures the routed message text only; no
        /// timestamps or other log internals are asserted.
        /// </summary>
        private sealed class RouteCapture : IDisposable
        {
            private readonly Action<string> _originalRoute;

            public RouteCapture()
            {
                _originalRoute = AudioConverterLog.Route;
                AudioConverterLog.Route = Messages.Add;
            }

            public List<string> Messages { get; } = new List<string>();

            public void Dispose()
            {
                AudioConverterLog.Route = _originalRoute;
            }
        }
    }
}
