// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Queues borrowed operator PCM and finalization through the host's shared TAR owner.</summary>
public interface ITransmitRecordingSink
{
    void WriteTransmitSamples(ChannelRecordingDescriptor channel, uint streamId, uint sourceId, ReadOnlySpan<short> samples);
    void StopTransmit(ChannelRecordingDescriptor channel);
}
