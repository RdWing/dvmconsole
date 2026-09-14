// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Settings;

namespace DvmConsole.Storage;

public sealed partial class ManagedReceivePreferences
{
    private sealed partial class ConfigurationScope
    {
        public ValueTask<ConsoleGroupPreferences> LoadGroupsAsync(CancellationToken cancellationToken = default)
            => owner.AccessAsync(settings =>
            {
                var state = GetState(settings);
                return new ConsoleGroupPreferences(state.GroupState.Clone(), state.RetainPatchStateOnStartup);
            }, false, cancellationToken);

        public async ValueTask SaveGroupAsync(string name, IReadOnlyList<PatchMemberSetting> members,
            bool enabled, bool oneWay, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(members);
            cancellationToken.ThrowIfCancellationRequested();
            string groupName = name.Trim();
            // Copy before awaiting storage. A later editor change cannot mutate the accepted write.
            var captured = members.Select(member => new PatchMemberSetting
            { SystemName = member.SystemName, DestinationId = member.DestinationId, ChannelName = member.ChannelName }).ToList();
            await owner.AccessAsync(settings =>
            {
                var groups = GetState(settings).GroupState;
                groups.Memberships[groupName] = captured;
                groups.EnabledStates[groupName] = enabled;
                groups.OneWayModes[groupName] = oneWay;
                return true;
            }, true, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask SaveRestorePatchesAsync(bool restore, CancellationToken cancellationToken = default)
            => await owner.AccessAsync(settings =>
            {
                GetState(settings).RetainPatchStateOnStartup = restore;
                return true;
            }, true, cancellationToken).ConfigureAwait(false);
    }
}
