// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

public enum RadioConnectionState
{
    Disconnected, Starting, WaitingForLogin, Authenticating, Configuring, Connected, Stopping, Faulted
}

public sealed record RadioConnectionSnapshot(SystemId SystemId, string Name,
    RadioConnectionState State, string Message, DateTimeOffset ChangedAt);

/// <summary>Current transport facts, independent of connection command completion.</summary>
public interface IRadioConnectionStateSource
{
    RadioConnectionSnapshot ConnectionState { get; }
}

public interface IConsoleConnectionStateSource
{
    ImmutableArray<RadioConnectionSnapshot> ConnectionStates { get; }
}

public interface IRadioConnectionStateNotifications : IRadioConnectionStateSource
{
    event EventHandler? ConnectionStateChanged;
}

public interface IConsoleConnectionStateNotifications : IConsoleConnectionStateSource
{
    event EventHandler? ConnectionStatesChanged;
}
