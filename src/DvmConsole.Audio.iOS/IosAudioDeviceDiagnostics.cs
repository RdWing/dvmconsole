// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Audio;

/// <summary>Native counters for one device generation; capture samples are mono, output is stereo.</summary>
public readonly record struct IosAudioDeviceDiagnostics(
    int Generation, int SampleRate, ulong OutputCallbacks, ulong DroppedInputSamples,
    ulong StarvedOutputSamples, ulong PendingStarvedOutputSamples)
{
    public TimeSpan StarvedDuration => OutputDuration(StarvedOutputSamples);
    public TimeSpan PendingStarvedDuration => OutputDuration(PendingStarvedOutputSamples);
    private TimeSpan OutputDuration(ulong samples)
        => SampleRate > 0 ? TimeSpan.FromSeconds(samples / (SampleRate * 2d)) : TimeSpan.Zero;
}
