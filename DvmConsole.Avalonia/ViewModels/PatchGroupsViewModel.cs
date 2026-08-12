// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using DvmConsole.Avalonia.Persistence;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Pure managed state for the Groups editor. Codeplug definitions are
    /// projected into immutable editor rows; memberships are editable copies
    /// resolved against the current channel set. Persistence and runtime patch
    /// behavior remain outside this type: <see cref="Commit"/> raises a
    /// merge-ready section, and <see cref="RequestPtt"/> raises a request-only
    /// event for the owning shell.
    /// </summary>
    public sealed class PatchGroupsViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// One codeplug group projected into the editor. The definition fields
        /// never change after construction; editor state and membership order do.
        /// </summary>
        public sealed class GroupState : INotifyPropertyChanged
        {
            private readonly List<MemberState> members;
            private bool isEditing;
            private bool isPttActive;
            private bool isOneWay;
            private bool isEnabled;

            internal GroupState(string name, bool isPatchGroup, IEnumerable<MemberState> members)
            {
                Name = name;
                IsPatchGroup = isPatchGroup;
                GroupType = isPatchGroup ? "patch" : "multiselect";
                this.members = new List<MemberState>(members ?? Enumerable.Empty<MemberState>());
                isEnabled = !isPatchGroup;
            }

            /// <summary>The codeplug group name.</summary>
            public string Name { get; }

            /// <summary>"patch" or "multiselect", from the codeplug definition.</summary>
            public string GroupType { get; }

            /// <summary>True for patch groups; false for multi-select groups.</summary>
            public bool IsPatchGroup { get; }

            /// <summary>Current members in source/order sequence.</summary>
            public IReadOnlyList<MemberState> Members => members.AsReadOnly();

            /// <summary>True while this group is the active editor target.</summary>
            public bool IsEditing
            {
                get => isEditing;
                internal set => Set(ref isEditing, value, nameof(IsEditing));
            }

            /// <summary>True while the shell has an active PTT request for this group.</summary>
            public bool IsPttActive
            {
                get => isPttActive;
                internal set => Set(ref isPttActive, value, nameof(IsPttActive));
            }

            /// <summary>Whether a patch group forwards one-way from member one.</summary>
            public bool IsOneWay
            {
                get => isOneWay;
                internal set => Set(ref isOneWay, value, nameof(IsOneWay));
            }

            /// <summary>Whether this patch is enabled. Multi-select groups are always enabled.</summary>
            public bool IsEnabled
            {
                get => isEnabled;
                internal set => Set(ref isEnabled, value, nameof(IsEnabled));
            }

            internal List<MemberState> MutableMembers => members;

            internal void NotifyMembersChanged()
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Members)));

            public event PropertyChangedEventHandler? PropertyChanged;

            private void Set<T>(ref T field, T value, string propertyName)
            {
                if (EqualityComparer<T>.Default.Equals(field, value))
                {
                    return;
                }

                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        /// <summary>
        /// A canonical codeplug channel identity used by a group membership.
        /// </summary>
        public sealed class MemberState
        {
            internal MemberState(string channelName, string systemName, string tgid)
            {
                ChannelName = channelName;
                SystemName = systemName;
                Tgid = tgid;
            }

            /// <summary>The codeplug channel display name.</summary>
            public string ChannelName { get; }

            /// <summary>The canonical radio-system name.</summary>
            public string SystemName { get; }

            /// <summary>The canonical talkgroup id.</summary>
            public string Tgid { get; }
        }

        private readonly IReadOnlyList<Codeplug.Channel> channels;
        private readonly GroupSettingsPersistence? persistence;
        private readonly string membershipContextKey;
        private readonly bool retainPatchStateOnStartup;
        private UserSettingsGroupSection loadedSection;
        private readonly List<GroupState> groups;

        /// <summary>
        /// Builds editor rows from the supplied codeplug definitions and
        /// resolves persisted members only against valid current channels.
        /// </summary>
        public PatchGroupsViewModel(
            IReadOnlyList<Codeplug.Group>? definitions,
            IReadOnlyList<Codeplug.Channel>? channels,
            GroupSettingsPersistence? persistence,
            string? membershipContextKey,
            bool retainPatchStateOnStartup)
        {
            this.channels = BuildChannelSnapshot(channels);
            this.persistence = persistence;
            this.membershipContextKey = membershipContextKey ?? string.Empty;
            this.retainPatchStateOnStartup = retainPatchStateOnStartup;
            loadedSection = LoadSection();
            groups = BuildGroups(definitions, loadedSection);
            Groups = new ReadOnlyCollection<GroupState>(groups);
        }

        /// <summary>Groups in codeplug order.</summary>
        public IReadOnlyList<GroupState> Groups { get; }

        /// <summary>True when any group is currently being edited.</summary>
        public bool IsAnyGroupEditing => groups.Any(group => group.IsEditing);

        /// <summary>
        /// Raised when the owner should persist the current merged section.
        /// The VM never writes the settings file itself.
        /// </summary>
        public event Action<UserSettingsGroupSection>? SaveRequested;

        /// <summary>
        /// Raised when the owner should start or stop group PTT. The payload is
        /// a copied, ordered member snapshot and is safe for the owner to retain.
        /// </summary>
        public event Action<string, bool, IReadOnlyList<PatchTalkgroupMember>>? PttRequested;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Enters edit mode for one group and exits it for every other group.
        /// Any active PTT is stopped before editing becomes visible.
        /// </summary>
        public bool EnterEdit(string groupName)
        {
            GroupState? target = FindGroup(groupName);
            if (target is null)
            {
                return false;
            }

            foreach (GroupState group in groups)
            {
                if (ReferenceEquals(group, target))
                {
                    continue;
                }

                StopPtt(group);
                group.IsEditing = false;
            }

            StopPtt(target);
            bool changed = !target.IsEditing;
            target.IsEditing = true;
            NotifyEditingChanged();
            return changed;
        }

        /// <summary>Exits edit mode for one group without writing settings.</summary>
        public bool ExitEdit(string groupName)
        {
            GroupState? group = FindGroup(groupName);
            if (group is null || !group.IsEditing)
            {
                return false;
            }

            group.IsEditing = false;
            NotifyEditingChanged();
            return true;
        }

        /// <summary>
        /// Adds a valid channel to an editing group. Identity duplicates and
        /// unknown channels are rejected; existing order is preserved.
        /// </summary>
        public bool AddMember(string groupName, string? systemName, string? tgid)
        {
            GroupState? group = FindGroup(groupName);
            if (group is null || !group.IsEditing)
            {
                return false;
            }

            MemberState? member = FindChannel(systemName, tgid);
            if (member is null || group.MutableMembers.Any(existing => SameIdentity(existing, member)))
            {
                return false;
            }

            group.MutableMembers.Add(member);
            NotifyMembersChanged(group);
            return true;
        }

        /// <summary>Removes one ordered member from an editing group.</summary>
        public bool RemoveMember(string groupName, int index)
        {
            GroupState? group = FindGroup(groupName);
            if (group is null || !group.IsEditing || index < 0 || index >= group.MutableMembers.Count)
            {
                return false;
            }

            group.MutableMembers.RemoveAt(index);
            NotifyMembersChanged(group);
            return true;
        }

        /// <summary>
        /// Moves one member to a new ordered position. This is the keyboard/
        /// button equivalent of the WPF drag reorder operation.
        /// </summary>
        public bool MoveMember(string groupName, int fromIndex, int toIndex)
        {
            GroupState? group = FindGroup(groupName);
            if (group is null
                || !group.IsEditing
                || fromIndex < 0
                || fromIndex >= group.MutableMembers.Count
                || toIndex < 0
                || toIndex >= group.MutableMembers.Count
                || fromIndex == toIndex)
            {
                return false;
            }

            MemberState member = group.MutableMembers[fromIndex];
            group.MutableMembers.RemoveAt(fromIndex);
            group.MutableMembers.Insert(toIndex, member);
            NotifyMembersChanged(group);
            return true;
        }

        /// <summary>Sets one-way mode for a patch group.</summary>
        public bool SetOneWay(string groupName, bool value)
        {
            GroupState? group = FindGroup(groupName);
            if (group is null || !group.IsPatchGroup || group.IsOneWay == value)
            {
                return false;
            }

            group.IsOneWay = value;
            return true;
        }

        /// <summary>
        /// Sets enabled state for a patch group. Disabling an active group
        /// emits the request-only stop edge; multi-select groups cannot be
        /// disabled.
        /// </summary>
        public bool SetEnabled(string groupName, bool value)
        {
            GroupState? group = FindGroup(groupName);
            if (group is null || !group.IsPatchGroup || group.IsEnabled == value)
            {
                return false;
            }

            group.IsEnabled = value;
            if (!value)
            {
                StopPtt(group);
            }

            return true;
        }

        /// <summary>
        /// Toggles group PTT and raises only the request event. Editing,
        /// disabled patch groups, empty groups, and unknown groups are no-ops.
        /// </summary>
        public bool RequestPtt(string groupName)
        {
            GroupState? group = FindGroup(groupName);
            if (group is null
                || IsAnyGroupEditing
                || (group.IsPatchGroup && !group.IsEnabled)
                || group.MutableMembers.Count == 0)
            {
                return false;
            }

            bool active = !group.IsPttActive;
            group.IsPttActive = active;
            PttRequested?.Invoke(group.Name, active, SnapshotMembers(group));
            return true;
        }

        /// <summary>
        /// Builds the current context section and raises a single save request.
        /// The owner decides when and how to call the persistence adapter.
        /// </summary>
        public void Commit()
        {
            loadedSection = LoadSection();
            UserSettingsGroupSection section = CloneSection(loadedSection);
            section.PatchGroupMemberships ??= new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>();
            section.PatchGroupModes ??= new Dictionary<string, Dictionary<string, bool>>();
            section.PatchGroupEnabledStates ??= new Dictionary<string, Dictionary<string, bool>>();

            section.PatchGroupMemberships[membershipContextKey] = BuildMembershipMap();
            section.PatchGroupModes[membershipContextKey] = BuildModeMap();
            section.PatchGroupEnabledStates[membershipContextKey] = BuildEnabledMap();
            loadedSection = CloneSection(section);
            SaveRequested?.Invoke(section);
        }

        /// <summary>
        /// Exits all edit modes and emits request-only stop edges for active PTT.
        /// It does not persist or dispose anything.
        /// </summary>
        public void Close()
        {
            foreach (GroupState group in groups)
            {
                StopPtt(group);
                group.IsEditing = false;
            }

            NotifyEditingChanged();
        }

        private UserSettingsGroupSection LoadSection()
        {
            if (persistence is null)
            {
                return new UserSettingsGroupSection();
            }

            try
            {
                return persistence.TryLoad(out UserSettingsGroupSection section)
                    ? section
                    : new UserSettingsGroupSection();
            }
            catch
            {
                return new UserSettingsGroupSection();
            }
        }

        private List<GroupState> BuildGroups(
            IReadOnlyList<Codeplug.Group>? definitions,
            UserSettingsGroupSection section)
        {
            var result = new List<GroupState>();
            Dictionary<string, List<PatchTalkgroupMember>> savedMemberships =
                GetContextMap(section.PatchGroupMemberships);
            Dictionary<string, bool> savedModes =
                GetContextMap(section.PatchGroupModes);
            Dictionary<string, bool> savedEnabledStates = retainPatchStateOnStartup
                ? GetContextMap(section.PatchGroupEnabledStates)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (Codeplug.Group? definition in definitions ?? Array.Empty<Codeplug.Group>())
            {
                if (definition is null || string.IsNullOrWhiteSpace(definition.Name))
                {
                    continue;
                }

                string name = definition.Name;
                bool isPatchGroup = definition.IsPatchGroup();
                var group = new GroupState(name, isPatchGroup, ResolveMembers(savedMemberships, name));
                group.IsOneWay = isPatchGroup && TryGet(savedModes, name, out bool oneWay) && oneWay;
                group.IsEnabled = !isPatchGroup
                    || (TryGet(savedEnabledStates, name, out bool enabled) && enabled);
                result.Add(group);
            }

            return result;
        }

        private List<MemberState> ResolveMembers(
            Dictionary<string, List<PatchTalkgroupMember>> savedMemberships,
            string groupName)
        {
            if (!TryGet(savedMemberships, groupName, out List<PatchTalkgroupMember>? saved)
                || saved is null)
            {
                return new List<MemberState>();
            }

            var result = new List<MemberState>();
            foreach (PatchTalkgroupMember? persisted in saved)
            {
                if (persisted is null)
                {
                    continue;
                }

                MemberState? canonical = FindChannel(persisted.SystemName, persisted.Tgid);
                if (canonical is not null && !result.Any(existing => SameIdentity(existing, canonical)))
                {
                    result.Add(canonical);
                }
            }

            return result;
        }

        private MemberState? FindChannel(string? systemName, string? tgid)
        {
            if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(tgid))
            {
                return null;
            }

            string wantedSystem = systemName.Trim();
            string wantedTgid = tgid.Trim();
            return channels
                .Where(channel => channel is not null)
                .Select(channel => new
                {
                    Channel = channel!,
                    SystemName = (channel!.System ?? string.Empty).Trim(),
                    Tgid = (channel.Tgid ?? string.Empty).Trim()
                })
                .Where(item => string.Equals(item.SystemName, wantedSystem, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Tgid, wantedTgid, StringComparison.Ordinal))
                .Select(item => new MemberState(item.Channel.Name ?? string.Empty, item.SystemName, item.Tgid))
                .FirstOrDefault();
        }

        private GroupState? FindGroup(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            string wanted = name.Trim();
            return groups.FirstOrDefault(group =>
                string.Equals(group.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
        }

        private Dictionary<string, List<PatchTalkgroupMember>> BuildMembershipMap()
        {
            var result = new Dictionary<string, List<PatchTalkgroupMember>>(StringComparer.OrdinalIgnoreCase);
            foreach (GroupState group in groups)
            {
                result[group.Name] = SnapshotMembers(group).ToList();
            }

            return result;
        }

        private Dictionary<string, bool> BuildModeMap()
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (GroupState group in groups.Where(group => group.IsPatchGroup))
            {
                result[group.Name] = group.IsOneWay;
            }

            return result;
        }

        private Dictionary<string, bool> BuildEnabledMap()
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (GroupState group in groups.Where(group => group.IsPatchGroup))
            {
                result[group.Name] = group.IsEnabled;
            }

            return result;
        }

        private static IReadOnlyList<PatchTalkgroupMember> SnapshotMembers(GroupState group)
            => group.MutableMembers
                .Select(member => new PatchTalkgroupMember
                {
                    SystemName = member.SystemName,
                    Tgid = member.Tgid
                })
                .ToList()
                .AsReadOnly();

        private void StopPtt(GroupState group)
        {
            if (!group.IsPttActive)
            {
                return;
            }

            group.IsPttActive = false;
            PttRequested?.Invoke(group.Name, false, SnapshotMembers(group));
        }

        private void NotifyMembersChanged(GroupState group)
        {
            group.NotifyMembersChanged();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Groups)));
        }

        private void NotifyEditingChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAnyGroupEditing)));
        }

        private static IReadOnlyList<Codeplug.Channel> BuildChannelSnapshot(
            IReadOnlyList<Codeplug.Channel>? source)
            => new ReadOnlyCollection<Codeplug.Channel>(
                (source ?? Array.Empty<Codeplug.Channel>())
                    .Where(channel => channel is not null)
                    .Select(channel => channel!)
                    .ToList());

        private static bool SameIdentity(MemberState left, MemberState right)
            => string.Equals(left.SystemName, right.SystemName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Tgid, right.Tgid, StringComparison.Ordinal);

        private static Dictionary<string, TValue> GetContextMap<TValue>(
            Dictionary<string, Dictionary<string, TValue>>? root,
            string contextKey)
        {
            if (root is not null && TryGet(root, contextKey, out Dictionary<string, TValue>? context)
                && context is not null)
            {
                return context;
            }

            return new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, TValue> GetContextMap<TValue>(
            Dictionary<string, Dictionary<string, TValue>>? root)
            => GetContextMap(root, membershipContextKey);

        private static bool TryGet<TValue>(
            Dictionary<string, TValue> values,
            string key,
            out TValue value)
        {
            if (values is not null && values.TryGetValue(key, out value!))
            {
                return true;
            }

            if (values is not null)
            {
                foreach (KeyValuePair<string, TValue> pair in values)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = pair.Value;
                        return true;
                    }
                }
            }

            value = default!;
            return false;
        }

        private static UserSettingsGroupSection CloneSection(UserSettingsGroupSection source)
        {
            var clone = new UserSettingsGroupSection
            {
                PatchGroupMemberships = CloneNestedMemberships(source?.PatchGroupMemberships),
                PatchGroupModes = CloneNestedMap(source?.PatchGroupModes),
                PatchGroupEnabledStates = CloneNestedMap(source?.PatchGroupEnabledStates)
            };
            return clone;
        }

        private static Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>> CloneNestedMemberships(
            Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>? source)
        {
            var clone = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>();
            foreach (KeyValuePair<string, Dictionary<string, List<PatchTalkgroupMember>>> context in
                source ?? new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>())
            {
                var groups = new Dictionary<string, List<PatchTalkgroupMember>>();
                foreach (KeyValuePair<string, List<PatchTalkgroupMember>> group in
                    context.Value ?? new Dictionary<string, List<PatchTalkgroupMember>>())
                {
                    groups[group.Key] = (group.Value ?? new List<PatchTalkgroupMember>())
                        .Where(member => member is not null)
                        .Select(member => new PatchTalkgroupMember
                        {
                            SystemName = member!.SystemName,
                            Tgid = member.Tgid
                        })
                        .ToList();
                }

                clone[context.Key] = groups;
            }

            return clone;
        }

        private static Dictionary<string, Dictionary<string, TValue>> CloneNestedMap<TValue>(
            Dictionary<string, Dictionary<string, TValue>>? source)
        {
            var clone = new Dictionary<string, Dictionary<string, TValue>>();
            foreach (KeyValuePair<string, Dictionary<string, TValue>> context in
                source ?? new Dictionary<string, Dictionary<string, TValue>>())
            {
                clone[context.Key] = new Dictionary<string, TValue>(
                    context.Value ?? new Dictionary<string, TValue>());
            }

            return clone;
        }
    }
}
