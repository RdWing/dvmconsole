// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Audio Bridge
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Audio Bridge
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2023 Bryan Biedenkapp, N2PLL
*
*/
#nullable enable

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Time, sample and byte count conversions for PCM audio, preserving the
    /// original arithmetic over <see cref="PcmFormat"/>.
    /// </summary>
    public class SampleTimeConvert
    {
        /*
        ** Methods
        */

        /// <summary>
        /// (ms) to sample count conversion
        /// </summary>
        /// <param name="format">PCM format</param>
        /// <param name="ms">Number of milliseconds</param>
        /// <returns>Number of samples</returns>
        public static int ToSamples(PcmFormat format, int ms)
        {
            return (int)(((long)ms) * format.SampleRate * format.Channels / 1000);
        }

        /// <summary>
        /// Sample count to (ms) conversion
        /// </summary>
        /// <param name="format">PCM format</param>
        /// <param name="samples">Number of samples</param>
        /// <returns>Number of milliseconds</returns>
        public static int ToMS(PcmFormat format, int samples)
        {
            return (int)(((float)samples / (float)format.SampleRate / (float)format.Channels) * 1000);
        }

        /// <summary>
        /// samples to bytes conversion
        /// </summary>
        /// <param name="format">PCM format</param>
        /// <param name="samples">Number of samples</param>
        /// <returns>Number of bytes for the number of samples</returns>
        public static int ToBytes(PcmFormat format, int samples)
        {
            return samples * (format.BitsPerSample / 8);
        }

        /// <summary>
        /// (ms) to bytes conversion
        /// </summary>
        /// <param name="format">PCM format</param>
        /// <param name="ms">Number of milliseconds</param>
        /// <returns>Number of bytes for the amount of audio in (ms)</returns>
        public static int MSToSampleBytes(PcmFormat format, int ms)
        {
            return ToBytes(format, ToSamples(format, ms));
        }
    } // public class SampleTimeConvert
} // namespace DvmConsole.Platform.Audio
