// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Closes new session input synchronously before asynchronous replacement cleanup.</summary>
public interface IConsoleSessionInputAdmission
{
    void SuspendInput();
}
