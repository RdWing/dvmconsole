// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal readonly record struct ReceiveJitterBufferProfile(
    TimeSpan PacketDuration,
    TimeSpan TargetDelay,
    bool IsAdaptive = false)
{
    public bool IsEnabled => TargetDelay > TimeSpan.Zero;
}

internal readonly record struct ReceiveJitterBufferConfiguration(
    TimeSpan PacketDuration,
    TimeSpan InitialDelay,
    TimeSpan MaximumDelay,
    bool IsAdaptive)
{
    public ReceiveJitterBufferProfile CreateProfile(TimeSpan targetDelay)
        => new(PacketDuration, targetDelay, IsAdaptive);
}
