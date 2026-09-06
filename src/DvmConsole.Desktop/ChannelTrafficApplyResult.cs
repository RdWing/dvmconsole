// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Operations;

namespace DvmConsole.Desktop;

internal readonly record struct ChannelTrafficApplyResult(
    bool Matched,
    ReceiveStreamTransition Transition,
    uint? ActiveStreamId = null,
    uint? EndedStreamId = null,
    DateTimeOffset? EndedAt = null)
{
    public static ChannelTrafficApplyResult NoMatch => new(false, ReceiveStreamTransition.None);
}
