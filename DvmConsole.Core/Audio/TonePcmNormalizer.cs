// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2025 Caleb, K4PHP and DVMProject (https://github.com/dvmproject) Authors
*
*/

namespace dvmconsole
{
    /// <summary>
    /// Signal-level RMS/peak normalization seam for alert tone PCM (Core-only, no NAudio/WPF/Platform dependencies).
    /// WPF parity: mirrors <c>MainWindow.NormalizeAlertTonePcm</c> (dvmconsole/MainWindow.xaml.cs:1717-1767)
    /// byte-for-byte, using the WPF constants <c>ALERT_TONE_TARGET_RMS_DBFS</c> (-18.0),
    /// <c>ALERT_TONE_PEAK_CEILING_DBFS</c> (-6.0), and <c>ALERT_TONE_MIN_RMS</c> (0.0001), with the
    /// same <c>DecibelsToLinear</c> conversion (MainWindow.xaml.cs:1769-1772).
    /// Input is signed 16-bit little-endian mono PCM at 8000 Hz.
    /// </summary>
    /// <remarks>
    /// SIGNAL-LEVEL NORMALIZATION ONLY. Tone generation (<see cref="TonePcmGenerator"/>), preset step
    /// sequencing (<see cref="TonePcmSequencer"/>), local playback, vocoder encoding, and TX routing are
    /// separate seams and are deliberately NOT handled here. This class applies the WPF dispatch
    /// console's single transparent gain stage: quiet tones are raised toward the -18 dBFS target RMS
    /// but never above the -6 dBFS peak ceiling. Null, sub-sample, silent, very quiet (RMS below
    /// <c>ALERT_TONE_MIN_RMS</c>), and near-unity-gain input is returned as the same reference; odd
    /// trailing bytes survive any scaled copy unchanged. No validation, dithering, or format
    /// conversion is performed.
    /// </remarks>
    public static class TonePcmNormalizer
    {
        private const double MaxPcmAmplitude = 32768.0;
        private const double TargetRmsDbfs = -18.0;
        private const double PeakCeilingDbfs = -6.0;
        private const double MinRms = 0.0001;
        private const double GainChangeEpsilon = 0.001;

        /// <summary>
        /// Applies the WPF-compatible RMS target and peak ceiling gain control to alert tone PCM.
        /// </summary>
        /// <param name="pcmData">Signed 16-bit little-endian mono PCM at 8000 Hz.</param>
        /// <returns>
        /// The same <paramref name="pcmData"/> reference when no gain is warranted (null, fewer than two
        /// bytes, RMS below <see cref="MinRms"/>, zero peak, or a gain within
        /// <see cref="GainChangeEpsilon"/> of unity); otherwise a new same-length buffer with each
        /// sample scaled by the computed gain, rounded, clamped to the signed 16-bit range, and written
        /// back as little-endian bytes. An odd trailing byte is copied unchanged.
        /// </returns>
        public static byte[] NormalizeAlertTonePcm(byte[] pcmData)
        {
            if (pcmData == null || pcmData.Length < 2)
                return pcmData;

            int sampleCount = pcmData.Length / 2;
            double sumSquares = 0;
            int peak = 0;

            for (int i = 0; i + 1 < pcmData.Length; i += 2)
            {
                short sample = (short)(pcmData[i] | (pcmData[i + 1] << 8));
                int absSample = Math.Abs((int)sample);

                if (absSample > peak)
                    peak = absSample;

                double normalizedSample = sample / MaxPcmAmplitude;
                sumSquares += normalizedSample * normalizedSample;
            }

            double rms = Math.Sqrt(sumSquares / sampleCount);
            if (rms < MinRms || peak == 0)
                return pcmData;

            double targetRms = DecibelsToLinear(TargetRmsDbfs);
            double peakCeiling = DecibelsToLinear(PeakCeilingDbfs);
            double peakLevel = peak / MaxPcmAmplitude;

            // Use one transparent gain stage: raise quiet tones toward target RMS, but never above the peak ceiling.
            double gain = Math.Min(targetRms / rms, peakCeiling / peakLevel);
            if (Math.Abs(gain - 1.0) < GainChangeEpsilon)
                return pcmData;

            byte[] normalizedData = new byte[pcmData.Length];
            for (int i = 0; i + 1 < pcmData.Length; i += 2)
            {
                short sample = (short)(pcmData[i] | (pcmData[i + 1] << 8));
                double scaled = Math.Round(sample * gain);
                short normalizedSample = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, scaled));

                normalizedData[i] = (byte)(normalizedSample & 0xFF);
                normalizedData[i + 1] = (byte)((normalizedSample >> 8) & 0xFF);
            }

            if (pcmData.Length % 2 != 0)
                normalizedData[pcmData.Length - 1] = pcmData[pcmData.Length - 1];

            return normalizedData;
        }

        private static double DecibelsToLinear(double db)
        {
            return Math.Pow(10.0, db / 20.0);
        }
    }
}
