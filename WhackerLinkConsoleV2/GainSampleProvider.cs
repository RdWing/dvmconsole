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
* Copyright (C) 2025 Caleb, K4PHP
* 
*/

using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;

namespace WhackerLinkConsoleV2
{
    public class GainSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float _gain = 1.0f;

        public GainSampleProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public float Gain
        {
            get => _gain;
            set => _gain = Math.Max(0, value);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            for (int i = 0; i < samplesRead; i++)
            {
                buffer[offset + i] *= _gain;
            }

            return samplesRead;
        }
    }
}
