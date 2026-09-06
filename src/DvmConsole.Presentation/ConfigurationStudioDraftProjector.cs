// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;

namespace DvmConsole.Presentation;

/// <summary>
/// Projects the live Studio collaborators into one immutable draft snapshot.
/// Keeping fingerprint composition here makes dirty tracking independent from
/// editor command orchestration.
/// </summary>
internal sealed class ConfigurationStudioDraftProjector
{
    public ConfigurationStudioDraftSnapshot Capture(
        ConfigurationDocument document,
        ConfigurationDraftIdentityRegistry identities,
        ConfigurationStudioCompanionState companions,
        ConfigurationStudioPreviewState preview,
        IReadOnlyDictionary<ZoneConfiguration, string> zoneSystemNames,
        IReadOnlySet<Guid> callPrioritySystemIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(companions);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(zoneSystemNames);
        ArgumentNullException.ThrowIfNull(callPrioritySystemIds);

        string yaml = document.IsReadOnly ? document.SourceText : document.Serialize();
        ConsoleConfiguration configuration = document.Configuration;
        ConfigurationDraftIdentityLayout identityLayout = identities.Capture(configuration);
        ConfigurationStudioReferencedFilesSnapshot referencedFiles = companions.CaptureSnapshot();
        Dictionary<Guid, WidgetPositionSetting> positions = preview.Capture(identities.GetChannelId);
        Dictionary<Guid, string> zoneSystems = zoneSystemNames.ToDictionary(
            entry => identities.GetZoneId(entry.Key),
            entry => entry.Value);
        var fingerprintComponents = new List<string>
        {
            yaml,
            string.Join(",", identityLayout.SystemIds),
            string.Join("|", identityLayout.Zones.Select(zone =>
                $"{zone.ZoneId}:{string.Join(',', zone.ChannelIds)}:{string.Join(',', zone.StreamIds)}")),
            string.Join(",", identityLayout.GroupIds),
            referencedFiles.KeyFileContent
        };
        fingerprintComponents.AddRange(referencedFiles.AliasContents
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"alias:{entry.Key}\n{entry.Value}"));
        fingerprintComponents.AddRange(positions
            .OrderBy(entry => entry.Key)
            .Select(entry => $"position:{entry.Key}:{entry.Value.X:R}:{entry.Value.Y:R}"));
        fingerprintComponents.AddRange(zoneSystems
            .OrderBy(entry => entry.Key)
            .Select(entry => $"zone-system:{entry.Key}:{entry.Value}"));
        fingerprintComponents.AddRange(callPrioritySystemIds
            .OrderBy(id => id)
            .Select(id => $"call-priority-system:{id}"));

        return new ConfigurationStudioDraftSnapshot(
            yaml,
            identityLayout,
            referencedFiles,
            positions,
            zoneSystems,
            callPrioritySystemIds.ToHashSet(),
            ConfigurationStudioDraftSnapshot.ComputeFingerprint(fingerprintComponents));
    }
}
