// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Operations;

namespace DvmConsole.Application;

/// <summary>On-demand health capture, serialized with session retirement.</summary>
public interface IConsoleEngineeringHealth
{
    Task<RuntimeHealthSnapshot> CaptureHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>Last completed catalog scan; reading this property never scans storage.</summary>
public interface IRecordingCatalogHealthSource
{
    CatalogScanHealth? CatalogHealth { get; }
}
