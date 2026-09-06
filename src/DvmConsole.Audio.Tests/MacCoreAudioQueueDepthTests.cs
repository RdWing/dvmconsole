// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class MacCoreAudioQueueDepthTests
{
    [Fact]
    public void NativeQueueDepthIsReportedInTheRequestedPlaybackRate()
    {
        int requestedSamples = MacCoreAudioBackend.ConvertQueueDepthToRequestedRate(
            nativeSamples: 5_760,
            nativeSampleRate: 48_000,
            requestedSampleRate: 8_000);

        Assert.Equal(960, requestedSamples);
    }
}
