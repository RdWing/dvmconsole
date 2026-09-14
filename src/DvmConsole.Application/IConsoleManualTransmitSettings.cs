// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed record ConsoleManualTransmitOptions(bool TalkPermitTone = false, bool MuteReceiveWhileTransmitting = true);

/// <summary>Local audio choices applied when the next manual call begins.</summary>
public interface IConsoleManualTransmitSettings
{
    ConsoleManualTransmitOptions ManualTransmitOptions { get; }
    bool CanSaveManualTransmitOptions { get; }
    ValueTask SetManualTransmitOptionsAsync(ConsoleManualTransmitOptions options, CancellationToken cancellationToken = default);
}

public interface IConsoleManualTransmitOptionsStore
{
    ValueTask<ConsoleManualTransmitOptions> LoadManualTransmitOptionsAsync(CancellationToken cancellationToken = default);
    ValueTask SaveManualTransmitOptionsAsync(ConsoleManualTransmitOptions options, CancellationToken cancellationToken = default);
}
