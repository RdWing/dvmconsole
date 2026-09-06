// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Presentation;

public sealed record ConfigurationStudioPosition(double X, double Y);

public sealed record ConfigurationStudioInitialState(
    IReadOnlyDictionary<string, ConfigurationStudioPosition> ChannelPositions,
    IReadOnlyDictionary<string, string> ZoneSystemAssignments,
    IReadOnlyCollection<string> CallPrioritySystemNames);
