// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

public sealed record ChannelTransmitPreferences(bool Selected = false, bool? Encrypted = null);
public sealed record ChannelTransmitPreferenceChange(bool? Selected = null, bool? Encrypted = null);

/// <summary>Saved TX selection and encryption choice; never restores an active transmission.</summary>
public interface IConsoleTransmitPreferences
{
    ValueTask<ImmutableDictionary<ChannelId, ChannelTransmitPreferences>> LoadTransmitAsync(CancellationToken cancellationToken);
    ValueTask SaveTransmitAsync(ChannelId id, ChannelTransmitPreferenceChange change, CancellationToken cancellationToken);
}
