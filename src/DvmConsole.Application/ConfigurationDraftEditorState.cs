// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed record ConfigurationDraftPosition(double X, double Y);

/// <summary>Uncommitted editor layout, stored with the draft rather than live operator settings.</summary>
public sealed record ConfigurationDraftEditorState(
    IReadOnlyDictionary<string, ConfigurationDraftPosition> ChannelPositions,
    IReadOnlyDictionary<string, string> ZoneSystemAssignments,
    IReadOnlyList<string> CallPrioritySystemNames);
