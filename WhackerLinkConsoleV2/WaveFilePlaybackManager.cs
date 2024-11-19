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
using System.Windows.Threading;

namespace WhackerLinkConsoleV2
{
    public class WaveFilePlaybackManager
    {
        private readonly string _waveFilePath;
        private readonly DispatcherTimer _timer;
        private WaveOutEvent _waveOut;
        private AudioFileReader _audioFileReader;
        private bool _isPlaying;

        public WaveFilePlaybackManager(string waveFilePath, int intervalMilliseconds = 500)
        {
            if (string.IsNullOrEmpty(waveFilePath))
                throw new ArgumentNullException(nameof(waveFilePath), "Wave file path cannot be null or empty.");

            _waveFilePath = waveFilePath;
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(intervalMilliseconds)
            };
            _timer.Tick += OnTimerTick;
        }

        public void Start()
        {
            if (_isPlaying)
                return;

            InitializeAudio();
            _isPlaying = true;
            _timer.Start();
        }

        public void Stop()
        {
            if (!_isPlaying)
                return;

            _timer.Stop();
            DisposeAudio();
            _isPlaying = false;
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            PlayAudio();
        }

        private void InitializeAudio()
        {
            _audioFileReader = new AudioFileReader(_waveFilePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_audioFileReader);
        }

        private void PlayAudio()
        {
            if (_waveOut != null && _waveOut.PlaybackState != PlaybackState.Playing)
            {
                _waveOut.Stop();
                _audioFileReader.Position = 0;
                _waveOut.Play();
            }
        }

        private void DisposeAudio()
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _audioFileReader?.Dispose();
            _waveOut = null;
            _audioFileReader = null;
        }
    }
}
