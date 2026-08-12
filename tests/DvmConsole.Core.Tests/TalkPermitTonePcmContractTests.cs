// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Buffers.Binary;
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract for the WPF-compatible local talk-permit tone PCM.
    /// </summary>
    public sealed class TalkPermitTonePcmContractTests
    {
        [Fact]
        public void GenerateTalkPermitTone_UsesWpf1200Hz50msFadeAndAmplitude()
        {
            var pcm = TonePcmGenerator.GenerateTalkPermitTone();

            Assert.Equal(800, pcm.Length); // 400 samples, 16-bit mono.
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(0, 2)));
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(pcm.Length - 2, 2)));

            var peak = 0;
            var nonZeroSamples = 0;
            for (var offset = 0; offset < pcm.Length; offset += 2)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset, 2));
                peak = Math.Max(peak, Math.Abs((int)sample));
                if (sample != 0)
                {
                    nonZeroSamples++;
                }
            }

            Assert.InRange(peak, 6000, 7000); // 0.20 * short.MaxValue, with fade.
            Assert.True(nonZeroSamples > 300);
        }
    }
}
