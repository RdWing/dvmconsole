// SPDX-License-Identifier: AGPL-3.0-only
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for normalized tone/DTMF preset sequencing.
    /// </summary>
    public sealed class TonePcmSequencerContractTests
    {
        [Fact]
        public void FrameAlignedByteCount_MatchesWpfTwentyMillisecondFrames()
        {
            Assert.Equal(320, TonePcmSequencer.FrameBytes);
            Assert.Equal(320, TonePcmSequencer.FrameAlignedByteCount(1));
            Assert.Equal(4160, TonePcmSequencer.FrameAlignedByteCount(250));
            Assert.Equal(12160, TonePcmSequencer.FrameAlignedByteCount(750));
        }

        [Fact]
        public void BuildTonePresetPcm_ClampsStepsAndAddsFrameAlignedSilence()
        {
            byte[] pcm = TonePcmSequencer.BuildTonePresetPcm(
                new[]
                {
                    new UserSettingsTonePresetStep
                    {
                        Kind = "tone",
                        FrequencyHz = 0,
                        DurationSeconds = 0.01
                    },
                    new UserSettingsTonePresetStep
                    {
                        Kind = "HOLD",
                        DurationSeconds = 0.01
                    }
                });

            byte[] expectedTone = TonePcmGenerator.GenerateTone(1, 0.25);
            Assert.Equal(32640, pcm.Length);
            Assert.Equal(expectedTone, pcm.Skip(12160).Take(expectedTone.Length));
            Assert.All(
                pcm.Skip(12160 + expectedTone.Length).Take(4160),
                value => Assert.Equal(0, value));
            Assert.All(pcm.Skip(20480), value => Assert.Equal(0, value));
        }

        [Fact]
        public void BuildDtmfPresetPcm_NormalizesInvalidDigitsAndAddsFrameAlignedSilence()
        {
            byte[] pcm = TonePcmSequencer.BuildDtmfPresetPcm(
                new[]
                {
                    new UserSettingsDtmfPresetStep
                    {
                        Kind = "digit",
                        Digit = "invalid",
                        DurationSeconds = 0.01
                    }
                });

            byte[] expectedDtmf = TonePcmGenerator.GenerateDtmfTone("1", 0.25);
            Assert.Equal(28480, pcm.Length);
            Assert.Equal(expectedDtmf, pcm.Skip(12160).Take(expectedDtmf.Length));
            Assert.All(pcm.Take(12160), value => Assert.Equal(0, value));
            Assert.All(pcm.Skip(12160 + expectedDtmf.Length), value => Assert.Equal(0, value));
        }

        [Fact]
        public void EmptyOrNullSteps_ReturnsEmptyPcm()
        {
            Assert.Empty(TonePcmSequencer.BuildTonePresetPcm(Array.Empty<UserSettingsTonePresetStep>()));
            Assert.Empty(TonePcmSequencer.BuildDtmfPresetPcm(null!));
        }
    }
}
