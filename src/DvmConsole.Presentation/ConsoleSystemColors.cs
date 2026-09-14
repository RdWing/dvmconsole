// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Presentation;

/// <summary>The established desktop FNE palette, shared with mobile navigation.</summary>
public static class ConsoleSystemColors
{
    private static readonly string[] Colors =
        ["#38BDF8", "#F97316", "#A78BFA", "#22C55E", "#F43F5E", "#EAB308", "#14B8A6", "#EC4899"];

    public static string At(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return Colors[index % Colors.Length];
    }
}
