// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Host focus and input-source observations around a shared channel press.</summary>
internal interface IChannelTransmitInputPort
{
    void Requested(ChannelId channel);
    void Starting();
    void Started();
    void Cleared();
}

internal sealed class ChannelTransmitInputPort(Action<ChannelId> requested,
    Action starting, Action started, Action cleared) : IChannelTransmitInputPort
{
    public void Requested(ChannelId channel) => requested(channel);
    public void Starting() => starting();
    public void Started() => started();
    public void Cleared() => cleared();
}
