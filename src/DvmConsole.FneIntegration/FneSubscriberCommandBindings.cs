// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.FneClient;

namespace DvmConsole.FneIntegration;

/// <summary>Explicit mapping between portable intent and the existing P25 encoder.</summary>
public static class FneSubscriberCommandBindings
{
    public static P25SubscriberCommand ToFne(ConsoleSubscriberCommand command) => command switch
    {
        ConsoleSubscriberCommand.Page => P25SubscriberCommand.CallAlert,
        ConsoleSubscriberCommand.RadioCheck => P25SubscriberCommand.RadioCheck,
        ConsoleSubscriberCommand.Inhibit => P25SubscriberCommand.Inhibit,
        ConsoleSubscriberCommand.Uninhibit => P25SubscriberCommand.Uninhibit,
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    public static ConsoleSubscriberCommand ToApplication(P25SubscriberCommand command) => command switch
    {
        P25SubscriberCommand.CallAlert => ConsoleSubscriberCommand.Page,
        P25SubscriberCommand.RadioCheck => ConsoleSubscriberCommand.RadioCheck,
        P25SubscriberCommand.Inhibit => ConsoleSubscriberCommand.Inhibit,
        P25SubscriberCommand.Uninhibit => ConsoleSubscriberCommand.Uninhibit,
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };
}
