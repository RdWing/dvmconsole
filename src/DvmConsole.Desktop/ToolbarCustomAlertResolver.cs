// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

// Display names are labels, not identities. A missing explicit target must
// never fall back to another file that happens to have the same name.
internal static class ToolbarCustomAlertResolver
{
    public static AlertToneViewModel? Resolve(
        IEnumerable<AlertToneViewModel> alerts, string? name, string? assetId, string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(assetId))
            return Guid.TryParse(assetId, out Guid id)
                ? alerts.FirstOrDefault(alert => Guid.TryParse(alert.AssetId, out Guid candidate) && candidate == id)
                : null;

        if (!string.IsNullOrWhiteSpace(filePath))
            return FindUnique(alerts, alert => string.Equals(alert.FilePath, filePath, StringComparison.Ordinal));

        // Older shortcuts stored only a name. Honor them only when the name
        // identifies one asset; guessing among duplicates could send the wrong alert.
        return FindUnique(alerts, alert => string.Equals(alert.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static AlertToneViewModel? FindUnique(
        IEnumerable<AlertToneViewModel> alerts, Func<AlertToneViewModel, bool> matches)
    {
        AlertToneViewModel? result = null;
        foreach (AlertToneViewModel alert in alerts)
        {
            if (!matches(alert))
                continue;
            if (result is not null)
                return null;
            result = alert;
        }
        return result;
    }
}
