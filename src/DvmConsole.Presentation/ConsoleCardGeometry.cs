// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Presentation;

/// <summary>Established channel-card dimensions shared by desktop, tablet and Studio previews.</summary>
public static class ConsoleCardGeometry
{
    public static double ResolveWidth(string? cardSize)
        => (cardSize ?? "normal").Trim().ToLowerInvariant() switch
        {
            "small" => 180,
            "large" => 330,
            _ => 235
        };

    public static double MeterWidth(double cardWidth) => cardWidth - (cardWidth == 180 ? 20 : 12);
}
