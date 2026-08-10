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
    /// Portable PCM tone/DTMF synthesis seam (Core-only, no NAudio/WPF/Platform dependencies).
    /// WPF parity: mirrors <c>dvmconsole.ToneGenerator.GenerateTone/GenerateDualTone</c>
    /// (dvmconsole/ToneGenerator.cs:49-90) and the DTMF keypad mapping in
    /// <c>MainWindow.TryGetDtmfFrequencies</c> (dvmconsole/MainWindow.xaml.cs:2428-2453).
    /// Output is signed 16-bit little-endian mono PCM at 8000 Hz.
    /// </summary>
    /// <remarks>
    /// SYNTHESIS ONLY. Duration/step normalization, frame padding, vocoder encoding,
    /// playback, and TX routing remain deferred to later gates. This seam deliberately
    /// preserves the WPF math exactly (including the <c>(short)</c> truncation cast and
    /// the dual-tone <c>/ 2.0</c> average) rather than adding amplitude or validation
    /// behavior. Deliberate deviation from the WPF playback path: the WPF
    /// <c>NormalizeDtmfDigit</c> (MainWindow.xaml.cs:2417-2426) coerces unknown digits to
    /// "1" for playback; this seam's RED contract instead returns <c>false</c> (and 0/0
    /// frequencies) for any non-single-character input, and
    /// <see cref="GenerateDtmfTone"/> returns an empty buffer for invalid digits.
    /// </remarks>
    public static class TonePcmGenerator
    {
        /// <summary>
        /// Sample rate in Hz shared by every generated tone (matches the WPF ToneGenerator's 8000 Hz).
        /// </summary>
        public const int SampleRate = 8000;

        /// <summary>
        /// Generate a sine wave tone at the specified frequency and duration.
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <param name="durationSeconds">Duration in seconds</param>
        /// <returns>Signed 16-bit little-endian mono PCM data as a byte array</returns>
        public static byte[] GenerateTone(double frequency, double durationSeconds)
        {
            int sampleCount = (int)(SampleRate * durationSeconds);
            byte[] buffer = new byte[sampleCount * 2];

            for (int i = 0; i < sampleCount; i++)
            {
                double time = (double)i / SampleRate;
                short sampleValue = (short)(Math.Sin(2 * Math.PI * frequency * time) * short.MaxValue);

                buffer[i * 2] = (byte)(sampleValue & 0xFF);
                buffer[i * 2 + 1] = (byte)((sampleValue >> 8) & 0xFF);
            }

            return buffer;
        }

        /// <summary>
        /// Generate two sine waves mixed together at the specified frequencies and duration.
        /// </summary>
        /// <param name="lowFrequency">Low group frequency in Hz</param>
        /// <param name="highFrequency">High group frequency in Hz</param>
        /// <param name="durationSeconds">Duration in seconds</param>
        /// <returns>Signed 16-bit little-endian mono PCM data as a byte array</returns>
        public static byte[] GenerateDualTone(double lowFrequency, double highFrequency, double durationSeconds)
        {
            int sampleCount = (int)(SampleRate * durationSeconds);
            byte[] buffer = new byte[sampleCount * 2];

            for (int i = 0; i < sampleCount; i++)
            {
                double time = (double)i / SampleRate;
                double low = Math.Sin(2 * Math.PI * lowFrequency * time);
                double high = Math.Sin(2 * Math.PI * highFrequency * time);
                short sampleValue = (short)(((low + high) / 2.0) * short.MaxValue);

                buffer[i * 2] = (byte)(sampleValue & 0xFF);
                buffer[i * 2 + 1] = (byte)((sampleValue >> 8) & 0xFF);
            }

            return buffer;
        }

        /// <summary>
        /// Resolve a DTMF keypad digit to its low/high group frequencies.
        /// The digit is trimmed and upper-cased (invariant) and must be exactly one
        /// keypad character: 0-9, *, #, A, B, C, D.
        /// </summary>
        /// <param name="digit">DTMF keypad digit (case-insensitive)</param>
        /// <param name="lowFrequency">Low group frequency in Hz on success, otherwise 0</param>
        /// <param name="highFrequency">High group frequency in Hz on success, otherwise 0</param>
        /// <returns><c>true</c> if the digit maps to a valid DTMF pair; otherwise <c>false</c></returns>
        public static bool TryGetDtmfFrequencies(string digit, out double lowFrequency, out double highFrequency)
        {
            lowFrequency = 0;
            highFrequency = 0;

            string normalized = (digit ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized.Length != 1)
                return false;

            switch (normalized)
            {
                case "1": lowFrequency = 697; highFrequency = 1209; return true;
                case "2": lowFrequency = 697; highFrequency = 1336; return true;
                case "3": lowFrequency = 697; highFrequency = 1477; return true;
                case "A": lowFrequency = 697; highFrequency = 1633; return true;
                case "4": lowFrequency = 770; highFrequency = 1209; return true;
                case "5": lowFrequency = 770; highFrequency = 1336; return true;
                case "6": lowFrequency = 770; highFrequency = 1477; return true;
                case "B": lowFrequency = 770; highFrequency = 1633; return true;
                case "7": lowFrequency = 852; highFrequency = 1209; return true;
                case "8": lowFrequency = 852; highFrequency = 1336; return true;
                case "9": lowFrequency = 852; highFrequency = 1477; return true;
                case "C": lowFrequency = 852; highFrequency = 1633; return true;
                case "*": lowFrequency = 941; highFrequency = 1209; return true;
                case "0": lowFrequency = 941; highFrequency = 1336; return true;
                case "#": lowFrequency = 941; highFrequency = 1477; return true;
                case "D": lowFrequency = 941; highFrequency = 1633; return true;
                default: return false;
            }
        }

        /// <summary>
        /// Generate a dual-tone DTMF signal for the given keypad digit.
        /// </summary>
        /// <param name="digit">DTMF keypad digit (case-insensitive)</param>
        /// <param name="durationSeconds">Duration in seconds</param>
        /// <returns>Signed 16-bit little-endian mono PCM data, or an empty array for an invalid digit</returns>
        public static byte[] GenerateDtmfTone(string digit, double durationSeconds)
        {
            if (!TryGetDtmfFrequencies(digit, out double lowFrequency, out double highFrequency))
                return Array.Empty<byte>();

            return GenerateDualTone(lowFrequency, highFrequency, durationSeconds);
        }
    }
}
