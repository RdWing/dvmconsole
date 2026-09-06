// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop.Tests;

internal static class ConfigurationStudioDraftTestBuilder
{
    public static ConfigurationStudioDraftSnapshot CreateSnapshot(
        string yaml,
        int keyId = 1,
        double x = 10,
        string system = "North")
    {
        Guid systemId = Guid.NewGuid();
        Guid zoneId = Guid.NewGuid();
        Guid channelId = Guid.NewGuid();
        var references = new ConfigurationStudioReferencedFilesSnapshot(
            null,
            null,
            string.Empty,
            null,
            null,
            false,
            $"KeyId: {keyId}",
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [],
            [],
            string.Empty);
        string fingerprint = ConfigurationStudioDraftSnapshot.ComputeFingerprint(
            [yaml, references.KeyFileContent, x.ToString(), system]);
        return new ConfigurationStudioDraftSnapshot(
            yaml,
            new ConfigurationDraftIdentityLayout(
                [systemId],
                [new ConfigurationZoneIdentityLayout(zoneId, [channelId], [])],
                []),
            references,
            new Dictionary<Guid, WidgetPositionSetting>
            {
                [channelId] = new() { X = x, Y = 20 }
            },
            new Dictionary<Guid, string> { [zoneId] = system },
            new HashSet<Guid> { systemId },
            fingerprint);
    }
}
