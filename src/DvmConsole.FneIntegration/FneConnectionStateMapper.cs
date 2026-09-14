// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.FneClient;

namespace DvmConsole.FneIntegration;

public static class FneConnectionStateMapper
{
    public static RadioConnectionState ToApplicationState(FneConnectionState state) => state switch
    {
        FneConnectionState.Disconnected => RadioConnectionState.Disconnected,
        FneConnectionState.Starting => RadioConnectionState.Starting,
        FneConnectionState.WaitingForLogin => RadioConnectionState.WaitingForLogin,
        FneConnectionState.Authenticating => RadioConnectionState.Authenticating,
        FneConnectionState.Configuring => RadioConnectionState.Configuring,
        FneConnectionState.Connected => RadioConnectionState.Connected,
        FneConnectionState.Stopping => RadioConnectionState.Stopping,
        FneConnectionState.Faulted => RadioConnectionState.Faulted,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown FNE connection state.")
    };

    // Desktop compatibility presentation still consumes the transport vocabulary.
    public static FneConnectionState ToTransportState(RadioConnectionState state) => state switch
    {
        RadioConnectionState.Disconnected => FneConnectionState.Disconnected,
        RadioConnectionState.Starting => FneConnectionState.Starting,
        RadioConnectionState.WaitingForLogin => FneConnectionState.WaitingForLogin,
        RadioConnectionState.Authenticating => FneConnectionState.Authenticating,
        RadioConnectionState.Configuring => FneConnectionState.Configuring,
        RadioConnectionState.Connected => FneConnectionState.Connected,
        RadioConnectionState.Stopping => FneConnectionState.Stopping,
        RadioConnectionState.Faulted => FneConnectionState.Faulted,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown radio connection state.")
    };
}
