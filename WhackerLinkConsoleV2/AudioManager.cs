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
using System.Collections.Generic;

namespace WhackerLinkConsoleV2
{
    public class AudioManager
    {
        private WaveOutEvent _waveOut;
        private MixingSampleProvider _mixer;

        private Dictionary<string, (BufferedWaveProvider buffer, VolumeSampleProvider volumeProvider)> _talkgroupProviders;

        /// <summary>
        /// Creates an instance of <see cref="AudioManager"/>
        /// </summary>
        public AudioManager()
        {
            _waveOut = new WaveOutEvent();
            _talkgroupProviders = new Dictionary<string, (BufferedWaveProvider, VolumeSampleProvider)>();
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(8000, 1))
            {
                ReadFully = true
            };

            _waveOut.Init(_mixer);
            _waveOut.Play();
        }

        /// <summary>
        /// Bad name, adds samples to a provider or creates a new provider
        /// </summary>
        /// <param name="talkgroupId"></param>
        /// <param name="audioData"></param>
        public void AddTalkgroupStream(string talkgroupId, byte[] audioData)
        {
            if (!_talkgroupProviders.ContainsKey(talkgroupId))
                AddTalkgroupStream(talkgroupId);

            _talkgroupProviders[talkgroupId].buffer.AddSamples(audioData, 0, audioData.Length);
        }

        /// <summary>
        /// Internal helper to create a talkgroup stream
        /// </summary>
        /// <param name="talkgroupId"></param>
        private void AddTalkgroupStream(string talkgroupId)
        {
            var bufferProvider = new BufferedWaveProvider(new WaveFormat(8000, 16, 1))
            {
                DiscardOnBufferOverflow = true
            };

            var volumeProvider = new VolumeSampleProvider(bufferProvider.ToSampleProvider())
            {
                Volume = 1.0f
            };

            _talkgroupProviders[talkgroupId] = (bufferProvider, volumeProvider);
            _mixer.AddMixerInput(volumeProvider);
        }

        /// <summary>
        /// Adjusts the volume of a specific talkgroup stream
        /// </summary>
        public void SetTalkgroupVolume(string talkgroupId, float volume)
        {
            if (_talkgroupProviders.ContainsKey(talkgroupId))
                _talkgroupProviders[talkgroupId].volumeProvider.Volume = volume;
            else
            {
                AddTalkgroupStream(talkgroupId);
                _talkgroupProviders[talkgroupId].volumeProvider.Volume = volume;
            }
        }

        /// <summary>
        /// Lop off the wave out
        /// </summary>
        public void Stop()
        {
            _waveOut.Stop();
        }
    }
}
