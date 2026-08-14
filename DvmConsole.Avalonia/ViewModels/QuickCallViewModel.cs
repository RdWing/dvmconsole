// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.ComponentModel;
using System.Globalization;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Detached request for the WPF manual QuickCall/QCII tone stack.
    /// </summary>
    public sealed class QuickCallRequest
    {
        public QuickCallRequest(double toneAHz, double toneBHz, byte[] pcm)
        {
            ToneAHz = toneAHz;
            ToneBHz = toneBHz;
            Pcm = pcm is null ? Array.Empty<byte>() : (byte[])pcm.Clone();
        }

        public double ToneAHz { get; }

        public double ToneBHz { get; }

        public byte[] Pcm { get; }

        public bool SendStartSignal => true;

        public bool ClearPageStateAfterSend => true;
    }

    /// <summary>
    /// Headless input state for a manual two-tone QCII dispatch.
    /// </summary>
    public sealed class QuickCallViewModel : INotifyPropertyChanged
    {
        private string toneA = string.Empty;
        private string toneB = string.Empty;
        private string validationMessage = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ToneA
        {
            get => toneA;
            set
            {
                string normalized = value ?? string.Empty;
                if (string.Equals(toneA, normalized, StringComparison.Ordinal))
                    return;

                toneA = normalized;
                OnPropertyChanged(nameof(ToneA));
                OnPropertyChanged(nameof(CanSend));
            }
        }

        public string ToneB
        {
            get => toneB;
            set
            {
                string normalized = value ?? string.Empty;
                if (string.Equals(toneB, normalized, StringComparison.Ordinal))
                    return;

                toneB = normalized;
                OnPropertyChanged(nameof(ToneB));
                OnPropertyChanged(nameof(CanSend));
            }
        }

        public bool CanSend
            => TryParseFrequency(toneA, out _)
                && TryParseFrequency(toneB, out _);

        public string ValidationMessage
        {
            get => validationMessage;
            private set
            {
                if (string.Equals(validationMessage, value, StringComparison.Ordinal))
                    return;

                validationMessage = value;
                OnPropertyChanged(nameof(ValidationMessage));
            }
        }

        public bool TryBuildRequest(out QuickCallRequest? request)
        {
            if (!TryParseFrequency(toneA, out double toneAHz)
                || !TryParseFrequency(toneB, out double toneBHz))
            {
                ValidationMessage = "Enter valid A and B tone frequencies.";
                request = null;
                return false;
            }

            byte[] pcm = TonePcmSequencer.BuildTonePresetPcm(
                new[]
                {
                    new UserSettingsTonePresetStep
                    {
                        Kind = "tone",
                        FrequencyHz = toneAHz,
                        DurationSeconds = 1.0,
                    },
                    new UserSettingsTonePresetStep
                    {
                        Kind = "tone",
                        FrequencyHz = toneBHz,
                        DurationSeconds = 3.0,
                    },
                });
            if (pcm.Length == 0)
            {
                ValidationMessage = "The tone stack could not be generated.";
                request = null;
                return false;
            }

            ValidationMessage = string.Empty;
            request = new QuickCallRequest(toneAHz, toneBHz, pcm);
            return true;
        }

        private static bool TryParseFrequency(string value, out double frequency)
            => double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out frequency)
                && double.IsFinite(frequency);

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
