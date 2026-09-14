// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Application;

namespace DvmConsole.Storage;

public sealed partial class ManagedReceivePreferences
{
    private sealed partial class ConfigurationScope : IConsoleWebStreamPreferences
    {
        // Materialization paths change across revisions and launches. This identity
        // is local to the settings store and stable for the managed configuration.
        private string StreamAuthorization(WebStreamPlaybackDescriptor stream)
            => ConfigurationWebStreamAuthorizationIdentity.Create(
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(owner.settingsPath))!, "ConfigurationIdentity", configurationId),
                stream.Name, stream.Url, stream.AuthUsername, stream.AuthPassword);

        public ValueTask<ImmutableDictionary<WebStreamId, ConsoleWebStreamPreference>> LoadWebStreamsAsync(
            IReadOnlyList<WebStreamPlaybackDescriptor> streams, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(streams);
            var captured = streams.ToArray();
            return owner.AccessAsync(settings =>
            {
                var state = GetState(settings);
                return captured.ToImmutableDictionary(stream => stream.Id, stream => new ConsoleWebStreamPreference(
                    state.WebStreamVolumes.GetValueOrDefault(stream.Name, 1),
                    state.RestoreSelectedChannelsOnStartup && StreamAuthorization(stream) is { Length: > 0 } identity &&
                    state.SelectedWebStreams.Contains(identity, StringComparer.Ordinal)));
            }, false, cancellationToken);
        }

        public async ValueTask SaveWebStreamAsync(WebStreamPlaybackDescriptor stream, bool? selected = null,
            double? volume = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (volume is { } gain && (!double.IsFinite(gain) || gain < 0 || gain > 4))
                throw new ArgumentOutOfRangeException(nameof(volume));
            string identity = StreamAuthorization(stream);
            if (selected == true && identity.Length == 0)
                throw new ArgumentException("The stream has no valid authorization identity.", nameof(stream));
            await owner.AccessAsync(settings =>
            {
                var state = GetState(settings);
                if (volume is { } value) state.WebStreamVolumes[stream.Name] = value;
                if (selected is { } enabled)
                {
                    state.SelectedWebStreams.RemoveAll(candidate => string.Equals(candidate, identity, StringComparison.Ordinal));
                    if (enabled) state.SelectedWebStreams.Add(identity);
                }
                return true;
            }, true, cancellationToken).ConfigureAwait(false);
        }
    }
}
