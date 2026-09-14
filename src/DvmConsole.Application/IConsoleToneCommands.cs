// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

public enum ConsoleToneTargets { Alert, Page }

public interface IConsoleToneCommands
{
    bool LocalToneMonitorEnabled { get; set; }
    Task SendToneAsync(GeneratedToneSequence sequence, ConsoleToneTargets targets, CancellationToken cancellationToken = default);
    Task SendAlertAudioAsync(AssetId asset, CancellationToken cancellationToken = default);
    Task CancelTonesAsync();
}
