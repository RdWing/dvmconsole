// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

/// <summary>Persisted receive intent, independent of transient audio and connection state.</summary>
public sealed record ChannelReceivePreferences(double Gain = 1, double Balance = 0,
    bool ReceiveEnabled = false, bool RecordingEnabled = false, ImmutableArray<uint> IgnoredSubscriberIds = default);

/// <summary>Only explicitly changed fields are written; unavailable host settings are preserved.</summary>
public sealed record ChannelReceivePreferenceChange(double? Gain = null, double? Balance = null,
    bool? ReceiveEnabled = null, bool? RecordingEnabled = null);

public interface IConsoleReceivePreferences
{
    ValueTask<ImmutableDictionary<ChannelId, ChannelReceivePreferences>> LoadAsync(CancellationToken cancellationToken);
    ValueTask SaveAsync(ChannelId id, ChannelReceivePreferenceChange change, CancellationToken cancellationToken);
}
