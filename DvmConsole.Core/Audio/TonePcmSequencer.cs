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
    /// Portable preset step sequencing seam (Core-only, no NAudio/WPF/Platform dependencies).
    /// WPF parity: mirrors the tone/DTMF preset stack pipelines in
    /// <c>MainWindow.BuildToneStackPcm/BuildDtmfStackPcm</c> (dvmconsole/MainWindow.xaml.cs:2261-2298,
    /// 2361-2398), the per-step builders <c>BuildToneStackStepPcm/BuildDtmfStackStepPcm</c>
    /// (MainWindow.xaml.cs:2300-2310, 2400-2415), the normalization passes
    /// <c>NormalizeToneStackSteps/NormalizeDtmfStackSteps</c> (MainWindow.xaml.cs:2236-2254,
    /// 2336-2354), <c>AddToneStackTransmitPadding</c> (MainWindow.xaml.cs:1699-1709),
    /// <c>GetAlertToneFrameAlignedByteCount</c> (MainWindow.xaml.cs:1711-1715), and
    /// <c>NormalizeDtmfDigit</c> (MainWindow.xaml.cs:2417-2426), using the WPF constants
    /// <c>PCM_SAMPLES_LENGTH</c> (320), <c>ALERT_TONE_PCM_BYTES_PER_MS</c> (16),
    /// <c>ALERT_TONE_LEAD_IN_MS</c>/<c>ALERT_TONE_TAIL_MS</c> (750), and
    /// <c>TONE_PRESET_MIN/MAX_DURATION_SECONDS</c> (0.25/10.0).
    /// Output is signed 16-bit little-endian mono PCM at 8000 Hz.
    /// </summary>
    /// <remarks>
    /// SEQUENCING ONLY. The WPF <c>NormalizeAlertTonePcm</c> RMS/peak level normalization
    /// (MainWindow.xaml.cs:1717) is deliberately NOT applied here; signal-level control is
    /// deferred to a later level-control gate. Playback, vocoder encoding, and TX routing
    /// remain deferred to later gates as well. This seam preserves the WPF math exactly:
    /// hold steps become frame-aligned zero buffers, tone/DTMF steps keep their raw
    /// generated PCM (un-aligned, matching the WPF per-step builders), steps are
    /// concatenated in input order, 750 ms of lead-in/tail silence is prepended/appended,
    /// and the total length is rounded up to whole <see cref="FrameBytes"/> frames as the
    /// WPF stack builders do after padding. Empty or null step lists yield an empty buffer.
    /// </remarks>
    public static class TonePcmSequencer
    {
        /// <summary>
        /// Transmit frame size in bytes: 20 ms of 8 kHz mono 16-bit PCM
        /// (mirrors the WPF <c>MainWindow.PCM_SAMPLES_LENGTH</c>, 320).
        /// </summary>
        public const int FrameBytes = 320;

        private const int BytesPerMillisecond = 16;

        private const double MinDurationSeconds = 0.25;
        private const double MaxDurationSeconds = 10.0;

        private const double MinFrequencyHz = 1.0;
        private const double MaxFrequencyHz = 4000.0;

        private const int LeadInMilliseconds = 750;
        private const int TailMilliseconds = 750;

        private const string HoldKind = "hold";

        private const string ValidDtmfDigits = "0123456789*#ABCD";

        /// <summary>
        /// Round a duration in milliseconds up to a whole transmit frame.
        /// </summary>
        /// <param name="durationMs">Duration in milliseconds.</param>
        /// <returns>
        /// The byte count of <paramref name="durationMs"/> (at 16 bytes/ms) rounded up to
        /// the next multiple of <see cref="FrameBytes"/>, mirroring the WPF
        /// <c>GetAlertToneFrameAlignedByteCount</c>.
        /// </returns>
        public static int FrameAlignedByteCount(int durationMs)
        {
            int byteCount = durationMs * BytesPerMillisecond;
            return ((byteCount + FrameBytes - 1) / FrameBytes) * FrameBytes;
        }

        /// <summary>
        /// Build the full transmit PCM buffer for an ordered tone preset step list.
        /// </summary>
        /// <param name="steps">Ordered tone preset steps; null entries are skipped.</param>
        /// <returns>
        /// Signed 16-bit little-endian mono PCM: lead-in silence, each step's generated PCM
        /// (case-insensitive <c>Kind</c> of "hold" yields frame-aligned zero silence),
        /// tail silence, rounded up to whole <see cref="FrameBytes"/> frames. Empty for
        /// null/empty input or when every step yields no PCM.
        /// </returns>
        public static byte[] BuildTonePresetPcm(IEnumerable<UserSettingsTonePresetStep> steps)
        {
            if (steps == null)
                return Array.Empty<byte>();

            List<byte[]> buffers = new List<byte[]>();
            foreach (UserSettingsTonePresetStep step in steps)
            {
                if (step == null)
                    continue;

                byte[] pcm = BuildToneStepPcm(step);
                if (pcm.Length > 0)
                    buffers.Add(pcm);
            }

            if (buffers.Count == 0)
                return Array.Empty<byte>();

            return AddTransmitPadding(Concatenate(buffers));
        }

        /// <summary>
        /// Build the full transmit PCM buffer for an ordered DTMF preset step list.
        /// </summary>
        /// <param name="steps">Ordered DTMF preset steps; null entries are skipped.</param>
        /// <returns>
        /// Signed 16-bit little-endian mono PCM: lead-in silence, each step's generated PCM
        /// (case-insensitive <c>Kind</c> of "hold" yields frame-aligned zero silence; digits
        /// are normalized per <see cref="NormalizeDtmfDigit"/>), tail silence, rounded up to
        /// whole <see cref="FrameBytes"/> frames. Empty for null/empty input or when every
        /// step yields no PCM.
        /// </returns>
        public static byte[] BuildDtmfPresetPcm(IEnumerable<UserSettingsDtmfPresetStep> steps)
        {
            if (steps == null)
                return Array.Empty<byte>();

            List<byte[]> buffers = new List<byte[]>();
            foreach (UserSettingsDtmfPresetStep step in steps)
            {
                if (step == null)
                    continue;

                byte[] pcm = BuildDtmfStepPcm(step);
                if (pcm.Length > 0)
                    buffers.Add(pcm);
            }

            if (buffers.Count == 0)
                return Array.Empty<byte>();

            return AddTransmitPadding(Concatenate(buffers));
        }

        /// <summary>
        /// Build one tone preset step's PCM, applying the WPF
        /// <c>NormalizeToneStackSteps</c> clamps (frequency 1..4000 Hz, duration 0.25..10 s).
        /// </summary>
        private static byte[] BuildToneStepPcm(UserSettingsTonePresetStep step)
        {
            if (string.Equals(step.Kind, HoldKind, StringComparison.OrdinalIgnoreCase))
                return BuildHoldSilence(step.DurationSeconds);

            double frequency = Math.Clamp(step.FrequencyHz, MinFrequencyHz, MaxFrequencyHz);
            double duration = Math.Clamp(step.DurationSeconds, MinDurationSeconds, MaxDurationSeconds);
            return TonePcmGenerator.GenerateTone(frequency, duration);
        }

        /// <summary>
        /// Build one DTMF preset step's PCM, applying the WPF duration clamp and
        /// <c>NormalizeDtmfDigit</c> digit normalization.
        /// </summary>
        private static byte[] BuildDtmfStepPcm(UserSettingsDtmfPresetStep step)
        {
            if (string.Equals(step.Kind, HoldKind, StringComparison.OrdinalIgnoreCase))
                return BuildHoldSilence(step.DurationSeconds);

            double duration = Math.Clamp(step.DurationSeconds, MinDurationSeconds, MaxDurationSeconds);
            return TonePcmGenerator.GenerateDtmfTone(NormalizeDtmfDigit(step.Digit), duration);
        }

        /// <summary>
        /// Build a zero-silence hold buffer: duration clamped to 0.25..10 seconds, rounded to
        /// milliseconds (minimum 1), then frame-aligned (mirrors the WPF hold branch of
        /// <c>BuildToneStackStepPcm</c>).
        /// </summary>
        private static byte[] BuildHoldSilence(double durationSeconds)
        {
            double clamped = Math.Clamp(durationSeconds, MinDurationSeconds, MaxDurationSeconds);
            int durationMs = Math.Max(1, (int)Math.Round(clamped * 1000));
            return new byte[FrameAlignedByteCount(durationMs)];
        }

        /// <summary>
        /// Normalize a DTMF digit exactly like the WPF <c>NormalizeDtmfDigit</c>: trim,
        /// uppercase (invariant), take the first character when longer than one, keep
        /// 0-9/*/#/A-D, and coerce anything else to "1".
        /// </summary>
        private static string NormalizeDtmfDigit(string digit)
        {
            string normalized = (digit ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized.Length > 1)
                normalized = normalized.Substring(0, 1);

            return normalized.Length == 1 && ValidDtmfDigits.Contains(normalized)
                ? normalized
                : "1";
        }

        /// <summary>
        /// Concatenate PCM buffers in input order.
        /// </summary>
        private static byte[] Concatenate(List<byte[]> buffers)
        {
            int totalLength = 0;
            foreach (byte[] buffer in buffers)
                totalLength += buffer.Length;

            byte[] combined = new byte[totalLength];
            int offset = 0;
            foreach (byte[] buffer in buffers)
            {
                Buffer.BlockCopy(buffer, 0, combined, offset, buffer.Length);
                offset += buffer.Length;
            }

            return combined;
        }

        /// <summary>
        /// Prepend and append the frame-aligned lead-in/tail silence, then round the total
        /// length up to whole <see cref="FrameBytes"/> frames, mirroring the WPF
        /// <c>AddToneStackTransmitPadding</c> and the final frame rounding in the stack builders.
        /// </summary>
        private static byte[] AddTransmitPadding(byte[] pcmData)
        {
            int leadInBytes = FrameAlignedByteCount(LeadInMilliseconds);
            int tailBytes = FrameAlignedByteCount(TailMilliseconds);

            byte[] paddedData = new byte[leadInBytes + pcmData.Length + tailBytes];
            Buffer.BlockCopy(pcmData, 0, paddedData, leadInBytes, pcmData.Length);

            int totalChunks = (paddedData.Length + FrameBytes - 1) / FrameBytes;
            if (paddedData.Length % FrameBytes == 0)
                return paddedData;

            byte[] framePadded = new byte[totalChunks * FrameBytes];
            Buffer.BlockCopy(paddedData, 0, framePadded, 0, paddedData.Length);
            return framePadded;
        }
    }
}
