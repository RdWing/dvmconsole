// SPDX-License-Identifier: AGPL-3.0-only
/**
* Behavior-lock contract for moving SampleTimeConvert (formerly
* dvmconsole/SampleTimeConvert.cs, NAudio-backed and dead code) into
* DvmConsole.Platform.Audio with the NAudio WaveFormat parameter replaced
* by the existing PcmFormat record struct. Class and method names are
* preserved; every value below is the exact output of the original
* formulas:
*   ToSamples       = (int)(((long)ms) * SampleRate * Channels / 1000)
*   ToMS            = (int)(((float)samples / SampleRate / Channels) * 1000)
*   ToBytes         = samples * (BitsPerSample / 8)
*   MSToSampleBytes = ToBytes(format, ToSamples(format, ms))
* The original's null-WaveFormat NullReferenceException is intentionally
* NOT preserved: PcmFormat is a struct, so there is no null format to
* dereference and no NRE contract to carry over.
* These tests preserve the conversion contract after the implementation moves
* into DvmConsole.Platform.Audio.
*/
#nullable enable
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// Contract gate for <c>SampleTimeConvert</c> as it moves into
    /// <c>DvmConsole.Platform.Audio</c>: time/sample/byte conversion math
    /// (including overflow wrap and float-truncation edge cases) locked to
    /// the original NAudio-backed behavior.
    /// </summary>
    public sealed class SampleTimeConvertContractTests
    {
        /// <summary>
        /// Milliseconds to interleaved sample count at the console codec
        /// (8000 Hz, 16-bit, mono): integer long arithmetic, no rounding.
        /// </summary>
        [Theory]
        [InlineData(8000, 16, 1, 1000, 8000)]
        [InlineData(8000, 16, 1, 20, 160)]
        [InlineData(8000, 16, 1, 1, 8)]
        [InlineData(8000, 16, 1, 0, 0)]
        [InlineData(8000, 16, 1, -20, -160)]
        public void ToSamples_ConsoleCodec_IsExactLongMath(int rate, int bits, int channels, int ms, int expected)
        {
            var format = new PcmFormat(rate, bits, channels);

            Assert.Equal(expected, SampleTimeConvert.ToSamples(format, ms));
        }

        /// <summary>
        /// At 44100 Hz stereo each millisecond is 88.2 samples: truncation
        /// is by integer division, and 1525 ms must land on the exact long
        /// product 134505 (formula fidelity, not float).
        /// </summary>
        [Theory]
        [InlineData(44100, 16, 2, 1, 88)]
        [InlineData(44100, 16, 2, 2, 176)]
        [InlineData(44100, 16, 2, 1525, 134505)]
        public void ToSamples_Stereo44100_TruncatesViaLongDivision(int rate, int bits, int channels, int ms, int expected)
        {
            var format = new PcmFormat(rate, bits, channels);

            Assert.Equal(expected, SampleTimeConvert.ToSamples(format, ms));
        }

        /// <summary>
        /// The unchecked cast to int wraps modulo 2^32: int.MaxValue ms at
        /// 48000 Hz stereo overflows long->int to exactly -96.
        /// </summary>
        [Fact]
        public void ToSamples_IntMaxValueMs_WrapsToNegative96()
        {
            var format = new PcmFormat(48000, 16, 2);

            Assert.Equal(-96, SampleTimeConvert.ToSamples(format, int.MaxValue));
        }

        /// <summary>
        /// Sample count to milliseconds at the console codec via float
        /// division: sub-millisecond fractions truncate toward zero, so 7
        /// samples is 0 ms but 9 samples is already 1 ms, and negative
        /// values truncate toward zero too (-7 -> 0, -8 -> -1).
        /// </summary>
        [Theory]
        [InlineData(8000, 16, 1, 8, 1)]
        [InlineData(8000, 16, 1, 160, 20)]
        [InlineData(8000, 16, 1, 1, 0)]
        [InlineData(8000, 16, 1, 7, 0)]
        [InlineData(8000, 16, 1, 9, 1)]
        [InlineData(8000, 16, 1, 0, 0)]
        [InlineData(8000, 16, 1, 1600, 200)]
        [InlineData(8000, 16, 1, 320, 40)]
        [InlineData(8000, 16, 1, 16000, 2000)]
        [InlineData(8000, 16, 1, -8, -1)]
        [InlineData(8000, 16, 1, -7, 0)]
        [InlineData(8000, 16, 1, -1, 0)]
        public void ToMS_ConsoleCodec_TruncatesTowardZero(int rate, int bits, int channels, int samples, int expected)
        {
            var format = new PcmFormat(rate, bits, channels);

            Assert.Equal(expected, SampleTimeConvert.ToMS(format, samples));
        }

        /// <summary>
        /// One second of 44100 Hz stereo audio (88200 interleaved samples)
        /// is exactly 1000 ms.
        /// </summary>
        [Fact]
        public void ToMS_Stereo44100_OneSecond_IsExactly1000()
        {
            var format = new PcmFormat(44100, 16, 2);

            Assert.Equal(1000, SampleTimeConvert.ToMS(format, 88200));
        }

        /// <summary>
        /// Bytes are samples times whole bytes per sample (integer division
        /// of bit depth): 15-bit truncates to 1 byte, and the int overflow
        /// at int.MaxValue 16-bit samples wraps to -2.
        /// </summary>
        [Theory]
        [InlineData(8000, 16, 1, 160, 320)]
        [InlineData(8000, 8, 1, 1, 1)]
        [InlineData(8000, 24, 1, 1, 3)]
        [InlineData(8000, 32, 1, 1, 4)]
        [InlineData(8000, 15, 1, 1, 1)]
        public void ToBytes_SamplesTimesWholeBytesPerSample(int rate, int bits, int channels, int samples, int expected)
        {
            var format = new PcmFormat(rate, bits, channels);

            Assert.Equal(expected, SampleTimeConvert.ToBytes(format, samples));
        }

        /// <summary>
        /// The int-multiply overflow is unchecked: int.MaxValue samples at
        /// 16-bit wraps modulo 2^32 to -2 bytes.
        /// </summary>
        [Fact]
        public void ToBytes_IntMaxValueSamples16Bit_WrapsToNegative2()
        {
            var format = new PcmFormat(8000, 16, 1);

            Assert.Equal(-2, SampleTimeConvert.ToBytes(format, int.MaxValue));
        }

        /// <summary>
        /// Milliseconds to bytes composes ToSamples and ToBytes: at the
        /// console codec, 20 ms is 160 samples is 320 bytes, 100 ms is 1600
        /// bytes, 1 ms is 16 bytes, and 0 ms is 0 bytes.
        /// </summary>
        [Theory]
        [InlineData(8000, 16, 1, 20, 320)]
        [InlineData(8000, 16, 1, 100, 1600)]
        [InlineData(8000, 16, 1, 1, 16)]
        [InlineData(8000, 16, 1, 0, 0)]
        public void MSToSampleBytes_ConsoleCodec_ComposesSamplesThenBytes(int rate, int bits, int channels, int ms, int expected)
        {
            var format = new PcmFormat(rate, bits, channels);

            Assert.Equal(expected, SampleTimeConvert.MSToSampleBytes(format, ms));
        }
    }
}
