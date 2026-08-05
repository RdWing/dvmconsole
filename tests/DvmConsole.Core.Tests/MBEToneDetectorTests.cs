// SPDX-License-Identifier: AGPL-3.0-only
/**
* Deterministic contract tests for the Core-owned MBEToneDetector and its
* NWaves 0.9.6 dependency.
*
* These tests lock the observable contract of the current production
* implementation: constructor defaults, input validation exceptions, the
* stateful hit-counter detection sequence, and the bin-index to frequency
* mapping (bin width 31.25 Hz at 8 kHz). Every expectation below was
* captured by executing the verbatim production code against NWaves 0.9.6;
* no FFT internals, floating point bin magnitudes, or timing are asserted.
*
* NOTE: degenerate limit combinations (low_limit == high_limit, or limits
* that widen the window beyond the 129 spectrogram bins) are NOT rejected by
* the constructor; they blow up on the first Detect() call instead.
* high_limit=5000 throws ArgumentOutOfRangeException (range slice past the
* end of the spectrum), while low_limit=3000 and high_limit=250 yield an
* empty slice whose Max() throws InvalidOperationException.
*/
using dvmconsole;
using NWaves.Signals;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Contract tests for the MBE tone detector.
    /// </summary>
    public sealed class MBEToneDetectorTests
    {
        // Sample rate and window locked by the production detector.
        private const int SampleRate = 8000;
        private const int WindowSize = 160;

        /// <summary>
        /// Builds one 160-sample window at 8 kHz containing a pure sine tone.
        /// </summary>
        private static DiscreteSignal Tone(double frequency, float amplitude = 3000f)
        {
            float[] samples = new float[WindowSize];
            for (int i = 0; i < WindowSize; i++)
            {
                samples[i] = amplitude *
                    (float)Math.Sin(2.0 * Math.PI * frequency * i / SampleRate);
            }
            return new DiscreteSignal(SampleRate, samples, true);
        }

        /// <summary>
        /// Builds a 160-sample window of pure silence.
        /// </summary>
        private static DiscreteSignal Silence()
        {
            return new DiscreteSignal(SampleRate, new float[WindowSize], true);
        }

        /// <summary>
        /// Feeds one window per frequency (0 = silence) into a single
        /// detector and returns the detection sequence.
        /// </summary>
        private static int[] DetectAll(MBEToneDetector detector, params double[] frequencies)
        {
            var results = new int[frequencies.Length];
            for (int i = 0; i < frequencies.Length; i++)
            {
                results[i] = detector.Detect(frequencies[i] == 0 ? Silence() : Tone(frequencies[i]));
            }
            return results;
        }

        /*
        ** Contract: assembly ownership and API surface
        */

        /// <summary>
        /// The detector must live in the Core assembly (not the WPF app).
        /// </summary>
        [Fact]
        public void Type_ResidesInCoreAssembly()
        {
            Assert.Equal("DvmConsole.Core", typeof(MBEToneDetector).Assembly.GetName().Name);
        }

        /// <summary>
        /// Constructor exposes the locked default parameters.
        /// </summary>
        [Fact]
        public void Constructor_ExposesLockedDefaults()
        {
            var parameters = typeof(MBEToneDetector).GetConstructors()[0].GetParameters();

            Assert.Equal(4, parameters.Length);
            Assert.Equal("detect_ratio", parameters[0].Name);
            Assert.Equal(90, parameters[0].DefaultValue);
            Assert.Equal("hits_reqd", parameters[1].Name);
            Assert.Equal(2, parameters[1].DefaultValue);
            Assert.Equal("low_limit", parameters[2].Name);
            Assert.Equal(250, parameters[2].DefaultValue);
            Assert.Equal("high_limit", parameters[3].Name);
            Assert.Equal(3000, parameters[3].DefaultValue);
        }

        /// <summary>
        /// Detect accepts a DiscreteSignal and returns a tone frequency in Hz
        /// (0 when no tone is detected).
        /// </summary>
        [Fact]
        public void Detect_AcceptsDiscreteSignal_ReturnsInt()
        {
            var method = typeof(MBEToneDetector).GetMethod("Detect");
            var parameters = method.GetParameters();

            Assert.Equal(typeof(int), method.ReturnType);
            Assert.Single(parameters);
            Assert.Equal(typeof(DiscreteSignal), parameters[0].ParameterType);
        }

        /*
        ** Contract: input validation
        */

        /// <summary>
        /// A 159-sample window is rejected with the exact production message
        /// (passed as the exception's ParamName by the production code).
        /// </summary>
        [Fact]
        public void Detect_WindowShorterThan160_ThrowsArgumentOutOfRangeException()
        {
            var detector = new MBEToneDetector();
            var signal = new DiscreteSignal(SampleRate, new float[WindowSize - 1], true);

            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => detector.Detect(signal));
            Assert.Equal("Signal must be 160 samples long!", ex.ParamName);
        }

        /// <summary>
        /// A window sampled at 44100 Hz is rejected with the exact production
        /// message (passed as the exception's ParamName by the production code).
        /// </summary>
        [Fact]
        public void Detect_SampleRate44100_ThrowsArgumentOutOfRangeException()
        {
            var detector = new MBEToneDetector();
            var signal = new DiscreteSignal(44100, new float[WindowSize], true);

            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => detector.Detect(signal));
            Assert.Equal("Signal must have sample rate of 8000 Hz!", ex.ParamName);
        }

        /// <summary>
        /// A null signal faults on the length check before any analysis.
        /// </summary>
        [Fact]
        public void Detect_NullSignal_ThrowsNullReferenceException()
        {
            var detector = new MBEToneDetector();

            Assert.Throws<NullReferenceException>(() => detector.Detect(null));
        }

        /*
        ** Contract: degenerate limit combinations fail at Detect, not at
        ** construction
        */

        /// <summary>
        /// A 5000 Hz upper limit widens the analysis window past the end of
        /// the 129-bin spectrum; the range slice throws on the first Detect.
        /// </summary>
        [Fact]
        public void Detect_HighLimit5000_ThrowsArgumentOutOfRangeException()
        {
            var detector = new MBEToneDetector(high_limit: 5000);

            Assert.Throws<ArgumentOutOfRangeException>(() => detector.Detect(Tone(1000)));
        }

        /// <summary>
        /// A 3000 Hz lower limit equals the 3000 Hz upper limit, producing an
        /// empty bin range whose Max() throws on the first Detect.
        /// </summary>
        [Fact]
        public void Detect_LowLimit3000_ThrowsInvalidOperationException()
        {
            var detector = new MBEToneDetector(low_limit: 3000);

            Assert.Throws<InvalidOperationException>(() => detector.Detect(Tone(1000)));
        }

        /// <summary>
        /// A 250 Hz upper limit equals the 250 Hz lower limit, producing an
        /// empty bin range whose Max() throws on the first Detect.
        /// </summary>
        [Fact]
        public void Detect_HighLimit250_ThrowsInvalidOperationException()
        {
            var detector = new MBEToneDetector(high_limit: 250);

            Assert.Throws<InvalidOperationException>(() => detector.Detect(Tone(1000)));
        }

        /*
        ** Contract: stateful detection sequence
        */

        /// <summary>
        /// The same frequency must be seen twice (hits_reqd = 2) before a
        /// tone is reported.
        /// </summary>
        [Fact]
        public void Detect_Two1000HzWindows_ReturnsZeroThen1000()
        {
            var detector = new MBEToneDetector();

            Assert.Equal(new[] { 0, 1000 }, DetectAll(detector, 1000, 1000));
        }

        /// <summary>
        /// hits_reqd = 3 delays detection to the third identical window.
        /// </summary>
        [Fact]
        public void Detect_HitsReqd3_ReturnsZeroZeroThen1000()
        {
            var detector = new MBEToneDetector(hits_reqd: 3);

            Assert.Equal(new[] { 0, 0, 1000 }, DetectAll(detector, 1000, 1000, 1000));
        }

        /// <summary>
        /// hits_reqd = 1 still requires two windows: the first window only
        /// seeds the hit counter, the second one detects.
        /// </summary>
        [Fact]
        public void Detect_HitsReqd1_ReturnsZeroThen1000()
        {
            var detector = new MBEToneDetector(hits_reqd: 1);

            Assert.Equal(new[] { 0, 1000 }, DetectAll(detector, 1000, 1000));
        }

        /// <summary>
        /// Changing frequency resets the hit counter; each new frequency must
        /// be seen twice in a row.
        /// </summary>
        [Fact]
        public void Detect_FrequencyChanges1500_1500_1000_1000_ReturnsZero1500Zero1000()
        {
            var detector = new MBEToneDetector();

            Assert.Equal(new[] { 0, 1500, 0, 1000 }, DetectAll(detector, 1500, 1500, 1000, 1000));
        }

        /// <summary>
        /// A silent window does not reset the hit counter; the tone seeded
        /// before the silence still detects on the next pass.
        /// </summary>
        [Fact]
        public void Detect_PassSilencePass_ReturnsZeroZeroThen1000()
        {
            var detector = new MBEToneDetector();

            Assert.Equal(new[] { 0, 0, 1000 }, DetectAll(detector, 1000, 0, 1000));
        }

        /// <summary>
        /// After a detection, repeated identical windows keep reporting the
        /// tone.
        /// </summary>
        [Fact]
        public void Detect_PostDetectionRepeats_ReturnsZeroThen1000Then1000()
        {
            var detector = new MBEToneDetector();

            Assert.Equal(new[] { 0, 1000, 1000 }, DetectAll(detector, 1000, 1000, 1000));
        }

        /*
        ** Contract: bin mapping and edges (31.25 Hz per bin)
        */

        /// <summary>
        /// 250 Hz is exactly on the lower-limit bin boundary and maps to
        /// itself.
        /// </summary>
        [Fact]
        public void Detect_Nominal250Hz_MapsTo250()
        {
            var detector = new MBEToneDetector();

            Assert.Equal(new[] { 0, 250 }, DetectAll(detector, 250, 250));
        }

        /// <summary>
        /// 2968.75 Hz is exactly on the upper-limit bin boundary and maps to
        /// 2968 (the truncated bin-center frequency).
        /// </summary>
        [Fact]
        public void Detect_2968_75Hz_MapsTo2968()
        {
            var detector = new MBEToneDetector();

            Assert.Equal(new[] { 0, 2968 }, DetectAll(detector, 2968.75, 2968.75));
        }

        /// <summary>
        /// 243.75 Hz sits just below the 250 Hz nominal lower limit but leaks
        /// into bin 8, so it is reported as 250 Hz.
        /// </summary>
        [Fact]
        public void Detect_243_75HzBelowNominalLimit_LeaksTo250()
        {
            var detector = new MBEToneDetector();

            Assert.Equal(new[] { 0, 250 }, DetectAll(detector, 243.75, 243.75));
        }

        /// <summary>
        /// 997 Hz lands closer to the 1000 Hz bin center and is reported as
        /// 1000 Hz.
        /// </summary>
        [Fact]
        public void Detect_997Hz_MapsTo1000()
        {
            var detector = new MBEToneDetector();

            Assert.Equal(new[] { 0, 1000 }, DetectAll(detector, 997, 997));
        }

        /// <summary>
        /// Exact bin-center frequencies map to themselves.
        /// </summary>
        [Fact]
        public void Detect_ExactBinCenters1500And2000_MapToThemselves()
        {
            var detector1500 = new MBEToneDetector();
            var detector2000 = new MBEToneDetector();

            Assert.Equal(new[] { 0, 1500 }, DetectAll(detector1500, 1500, 1500));
            Assert.Equal(new[] { 0, 2000 }, DetectAll(detector2000, 2000, 2000));
        }

        /// <summary>
        /// detect_ratio = 1000 is never exceeded by a 3000-amplitude tone, so
        /// nothing is ever reported.
        /// </summary>
        [Fact]
        public void Detect_DetectRatio1000_NeverDetects3000AmplitudeTone()
        {
            var detector = new MBEToneDetector(detect_ratio: 1000);

            Assert.Equal(new[] { 0, 0 }, DetectAll(detector, 1000, 1000));
        }
    } // public sealed class MBEToneDetectorTests
} // namespace DvmConsole.Core.Tests
