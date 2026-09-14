// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public readonly record struct PatchMemberCapabilities(string Name, bool CanReceive, bool CanTransmit);

/// <summary>Shared editor admission rules; runtime transmit admission is checked again at call start.</summary>
public static class PatchMembershipValidation
{
    public static string? Validate(IReadOnlyList<PatchMemberCapabilities> members, bool oneWay, int sourceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0) return null;
        if (oneWay && (sourceIndex < 0 || sourceIndex >= members.Count || !members[sourceIndex].CanReceive))
            return "Choose a receive-capable source for the one-way patch.";

        List<string>? invalid = null;
        for (int index = 0; index < members.Count; index++)
        {
            if ((oneWay && index == sourceIndex) || members[index].CanTransmit) continue;
            (invalid ??= []).Add(members[index].Name);
        }
        if (invalid is null) return null;
        string names = string.Join(", ", invalid);
        return oneWay
            ? $"One-way patch destinations must be transmit-capable: {names}."
            : $"These members cannot transmit: {names}.";
    }
}
