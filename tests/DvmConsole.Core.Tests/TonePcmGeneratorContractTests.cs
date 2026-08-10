// SPDX-License-Identifier: AGPL-3.0-only
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for the portable PCM tone/DTMF synthesis seam.
    /// </summary>
    public sealed class TonePcmGeneratorContractTests
    {
        [Fact]
        public void GenerateTone_UsesWpf8Khz16BitMonoLittleEndianSine()
        {
            byte[] pcm = TonePcmGenerator.GenerateTone(1000, 0.0005);

            Assert.Equal(8, pcm.Length);
            Assert.Equal(
                new byte[] { 0, 0, 129, 90, 255, 127, 129, 90 },
                pcm);
        }

        [Fact]
        public void GenerateDualTone_AveragesTwoSineWavesInWpfPcmShape()
        {
            byte[] pcm = TonePcmGenerator.GenerateDualTone(697, 1209, 0.0005);

            Assert.Equal(8, pcm.Length);
            Assert.Equal(
                new byte[] { 0, 0, 89, 85, 119, 117, 80, 82 },
                pcm);
        }

        [Fact]
        public void DtmfMapping_UsesStandardKeypadFrequenciesAndRejectsUnknownKeys()
        {
            Assert.True(TonePcmGenerator.TryGetDtmfFrequencies("5", out double low, out double high));
            Assert.Equal(770, low);
            Assert.Equal(1336, high);

            Assert.True(TonePcmGenerator.TryGetDtmfFrequencies("a", out low, out high));
            Assert.Equal(697, low);
            Assert.Equal(1633, high);

            Assert.False(TonePcmGenerator.TryGetDtmfFrequencies("invalid", out low, out high));
            Assert.Equal(0, low);
            Assert.Equal(0, high);
        }

        [Fact]
        public void GenerateDtmfTone_UsesMappedDualToneFrequencies()
        {
            byte[] pcm = TonePcmGenerator.GenerateDtmfTone("5", 0.0005);

            Assert.Equal(8, pcm.Length);
            Assert.Equal(
                new byte[] { 0, 0, 224, 91, 39, 115, 181, 61 },
                pcm);
            Assert.Empty(TonePcmGenerator.GenerateDtmfTone("invalid", 0.0005));
        }
    }
}
