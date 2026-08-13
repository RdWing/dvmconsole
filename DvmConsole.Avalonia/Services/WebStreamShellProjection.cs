// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using dvmconsole;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Headless shell projection for configured web-stream chips. It owns the
    /// restore/volume/position policy while the window owns controls and
    /// coordinator lifetimes.
    /// </summary>
    public sealed class WebStreamShellProjection
    {
        private readonly IReadOnlyList<WebStreamShellItem> items;

        public WebStreamShellProjection(
            IEnumerable<WebStreamShellDefinition>? definitions,
            bool restoreSelected,
            IEnumerable<string>? selectedNames,
            IReadOnlyDictionary<string, double>? volumes,
            IReadOnlyDictionary<string, UserSettingsLayoutPosition>? positions)
        {
            var selected = new HashSet<string>(
                selectedNames ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var savedVolumes = volumes ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var savedPositions = positions ?? new Dictionary<string, UserSettingsLayoutPosition>(StringComparer.OrdinalIgnoreCase);

            items = (definitions ?? Array.Empty<WebStreamShellDefinition>())
                .Where(definition => definition.Definition is not null
                    && !string.IsNullOrWhiteSpace(definition.Definition.Name)
                    && !string.IsNullOrWhiteSpace(definition.Definition.Url))
                .Select(definition =>
                {
                    var name = definition.Definition.Name!.Trim();
                    var zoneName = definition.ZoneName?.Trim() ?? string.Empty;
                    var positionKey = BuildPositionKey(zoneName, name);
                    var position = savedPositions.TryGetValue(positionKey, out var savedPosition)
                        || savedPositions.TryGetValue(name, out savedPosition)
                        ? new WebStreamShellPosition(savedPosition.X, savedPosition.Y)
                        : new WebStreamShellPosition(20, 20);
                    var volume = savedVolumes.TryGetValue(name, out var savedVolume)
                        ? NormalizeVolume(savedVolume)
                        : 1.0;
                    return new WebStreamShellItem(
                        definition.Definition,
                        zoneName,
                        name,
                        volume,
                        position,
                        restoreSelected && selected.Contains(name));
                })
                .ToList();
        }

        public IReadOnlyList<WebStreamShellItem> Items => items;

        internal static string BuildPositionKey(string? zoneName, string displayName)
            => string.IsNullOrWhiteSpace(zoneName)
                ? displayName
                : $"{zoneName.Trim()}|{displayName}";

        private static double NormalizeVolume(double value)
            => double.IsNaN(value) || double.IsInfinity(value)
                ? 1.0
                : Math.Clamp(Math.Round(value * 10.0) / 10.0, 0.0, 4.0);
    }

    public sealed record WebStreamShellDefinition(
        Codeplug.WebStream Definition,
        string ZoneName);

    public sealed record WebStreamShellItem(
        Codeplug.WebStream Definition,
        string ZoneName,
        string DisplayName,
        double Volume,
        WebStreamShellPosition Position,
        bool ShouldRestoreActive)
    {
        public string PositionKey => WebStreamShellProjection.BuildPositionKey(ZoneName, DisplayName);
    }

    public readonly record struct WebStreamShellPosition(double X, double Y);
}