// SPDX-License-Identifier: AGPL-3.0-only
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for WPF-compatible PCM RMS/peak normalization.
    /// </summary>
    public sealed class TonePcmNormalizerContractTests
    {
        [Fact]
        public void NormalizeAlertTonePcm_AppliesWpfTargetGainAndPreservesLittleEndianPcm()
        {
            byte[] pcm = BuildPcm(1000, -1000, 500, -500);

            byte[] normalized = TonePcmNormalizer.NormalizeAlertTonePcm(pcm);

            Assert.Equal(
                new byte[] { 98, 20, 158, 235, 49, 10, 207, 245 },
                normalized);
        }

        [Fact]
        public void NormalizeAlertTonePcm_UsesPeakCeilingForHighCrestFactorAudio()
        {
            short[] samples = new short[16];
            samples[0] = short.MaxValue;

            byte[] normalized = TonePcmNormalizer.NormalizeAlertTonePcm(BuildPcm(samples));

            Assert.Equal(16423, ReadSample(normalized, 0));
            Assert.Equal(0, ReadSample(normalized, 1));
        }

        [Fact]
        public void NormalizeAlertTonePcm_ReturnsNullSilenceAndVeryQuietOrOddDataUnchanged()
        {
            Assert.Null(TonePcmNormalizer.NormalizeAlertTonePcm(null!));

            byte[] silence = BuildPcm(0, 0, 0, 0);
            Assert.Same(silence, TonePcmNormalizer.NormalizeAlertTonePcm(silence));

            byte[] veryQuiet = BuildPcm(1, 1, 1, 1);
            Assert.Same(veryQuiet, TonePcmNormalizer.NormalizeAlertTonePcm(veryQuiet));

            byte[] oddLength = new byte[] { 0, 0, 127 };
            Assert.Same(oddLength, TonePcmNormalizer.NormalizeAlertTonePcm(oddLength));
        }

        private static byte[] BuildPcm(params short[] samples)
        {
            byte[] pcm = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                pcm[i * 2] = (byte)(samples[i] & 0xFF);
                pcm[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
            }

            return pcm;
        }

        private static short ReadSample(byte[] pcm, int sampleIndex)
        {
            int offset = sampleIndex * 2;
            return (short)(pcm[offset] | (pcm[offset + 1] << 8));
        }
    }
}
