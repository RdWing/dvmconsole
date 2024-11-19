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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WhackerLinkConsoleV2
{
    public class FlashingBackgroundManager
    {
        private readonly Control _control;
        private readonly Canvas _canvas;
        private readonly UserControl _userControl;
        private readonly Window _mainWindow;
        private readonly DispatcherTimer _timer;
        private Brush _originalControlBackground;
        private Brush _originalCanvasBackground;
        private Brush _originalUserControlBackground;
        private Brush _originalMainWindowBackground;
        private bool _isFlashing;

        public FlashingBackgroundManager(Control control = null, Canvas canvas = null, UserControl userControl = null, Window mainWindow = null, int intervalMilliseconds = 450)
        {
            _control = control;
            _canvas = canvas;
            _userControl = userControl;
            _mainWindow = mainWindow;

            if (_control == null && _canvas == null && _userControl == null && _mainWindow == null)
                throw new ArgumentException("At least one of control, canvas, userControl, or mainWindow must be provided.");

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(intervalMilliseconds)
            };
            _timer.Tick += OnTimerTick;
        }

        public void Start()
        {
            if (_isFlashing)
                return;

            if (_control != null)
                _originalControlBackground = _control.Background;

            if (_canvas != null)
                _originalCanvasBackground = _canvas.Background;

            if (_userControl != null)
                _originalUserControlBackground = _userControl.Background;

            if (_mainWindow != null)
                _originalMainWindowBackground = _mainWindow.Background;

            _isFlashing = true;
            _timer.Start();
        }

        public void Stop()
        {
            if (!_isFlashing)
                return;

            _timer.Stop();

            if (_control != null)
                _control.Background = _originalControlBackground;

            if (_canvas != null)
                _canvas.Background = _originalCanvasBackground;

            if (_userControl != null)
                _userControl.Background = _originalUserControlBackground;

            if (_mainWindow != null && _originalMainWindowBackground != null)
                _mainWindow.Background = _originalMainWindowBackground;

            _isFlashing = false;
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            Brush flashingColor = Brushes.Red;

            if (_control != null)
                _control.Background = _control.Background == Brushes.DarkRed ? _originalControlBackground : Brushes.DarkRed;

            if (_canvas != null)
                _canvas.Background = _canvas.Background == flashingColor ? _originalCanvasBackground : flashingColor;

            if (_userControl != null)
                _userControl.Background = _userControl.Background == Brushes.DarkRed ? _originalUserControlBackground : Brushes.DarkRed;

            if (_mainWindow != null)
                _mainWindow.Background = _mainWindow.Background == flashingColor ? _originalMainWindowBackground : flashingColor;
        }
    }
}
