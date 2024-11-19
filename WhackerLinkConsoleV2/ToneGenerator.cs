/*
* WhackerLink - WhackerLinkConsoleV2
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the GNU General Public License as published by
* the Free Software Foundation, either version 3 of the License, or
* (at your option) any later version.
*
* This program is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU General Public License for more details.
*
* You should have received a copy of the GNU General Public License
* along with this program.  If not, see <http://www.gnu.org/licenses/>.
* 
* Copyright (C) 2024 Caleb, K4PHP
* 
*/

using NAudio.Wave;

namespace WhackerLinkConsoleV2
{
    /// <summary>
    /// 
    /// </summary>
    public class ToneGenerator
    {
        private readonly int _sampleRate = 8000;
        private readonly int _bitsPerSample = 16;
        private readonly int _channels = 1;
        private WaveOutEvent _waveOut;
        private BufferedWaveProvider _waveProvider;

        /// <summary>
        /// Creates an instance of <see cref="ToneGenerator"/>
        /// </summary>
        public ToneGenerator()
        {
            _waveOut = new WaveOutEvent();
            _waveProvider = new BufferedWaveProvider(new WaveFormat(_sampleRate, _bitsPerSample, _channels));
            _waveOut.Init(_waveProvider);
        }

        /// <summary>
        /// Generate a sine wave tone at the specified frequency and duration.
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <param name="durationSeconds">Duration in seconds</param>
        /// <returns>PCM data as a byte array</returns>
        public byte[] GenerateTone(double frequency, double durationSeconds)
        {
            int sampleCount = (int)(_sampleRate * durationSeconds);
            byte[] buffer = new byte[sampleCount * (_bitsPerSample / 8)];

            for (int i = 0; i < sampleCount; i++)
            {
                double time = (double)i / _sampleRate;
                short sampleValue = (short)(Math.Sin(2 * Math.PI * frequency * time) * short.MaxValue);

                buffer[i * 2] = (byte)(sampleValue & 0xFF);
                buffer[i * 2 + 1] = (byte)((sampleValue >> 8) & 0xFF);
            }

            return buffer;
        }

        /// <summary>
        /// Play the generated tone through the speakers.
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <param name="durationSeconds">Duration in seconds</param>
        public void PlayTone(double frequency, double durationSeconds)
        {
            byte[] toneData = GenerateTone(frequency, durationSeconds);

            _waveProvider.ClearBuffer();
            _waveProvider.AddSamples(toneData, 0, toneData.Length);

            _waveOut.Play();
        }

        /// <summary>
        /// Stop playback.
        /// </summary>
        public void StopTone()
        {
            _waveOut.Stop();
        }

        /// <summary>
        /// Dispose of resources.
        /// </summary>
        public void Dispose()
        {
            _waveOut.Dispose();
        }
    }
}
