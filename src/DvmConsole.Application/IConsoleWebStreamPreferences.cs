// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

public sealed record ConsoleWebStreamPreference(double Volume, bool RestoreSelected);

/// <summary>Configuration-scoped choices; automatic access is tied to the exact configured stream credentials.</summary>
public interface IConsoleWebStreamPreferences
{
    ValueTask<ImmutableDictionary<WebStreamId, ConsoleWebStreamPreference>> LoadWebStreamsAsync(
        IReadOnlyList<WebStreamPlaybackDescriptor> streams, CancellationToken cancellationToken = default);

    ValueTask SaveWebStreamAsync(WebStreamPlaybackDescriptor stream, bool? selected = null,
        double? volume = null, CancellationToken cancellationToken = default);
}
