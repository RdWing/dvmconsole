// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;

namespace DvmConsole.Application;

/// <summary>Managed assets referenced by the shared operator settings schema.</summary>
public static class ConsoleAssetReferences
{
    public static IReadOnlySet<AssetId> FromSettings(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var referenced = new HashSet<AssetId>();
        if (Guid.TryParse(settings.UserBackgroundAssetId, out Guid background))
            referenced.Add(new(background));
        if (Guid.TryParse(settings.MobileAlertAssetId, out Guid mobileAlert))
            referenced.Add(new(mobileAlert));
        foreach (AlertToneSetting tone in settings.AlertTones)
            if (Guid.TryParse(tone.AssetId, out Guid id)) referenced.Add(new(id));
        return referenced;
    }
}

/// <summary>Checks persisted references while excluding concurrent settings writes.</summary>
public interface IConsoleToneAssetMaintenance
{
    ValueTask<bool> DeleteUnreferencedToneAssetAsync(AssetId id, IAssetStore assets,
        CancellationToken cancellationToken = default);
}
