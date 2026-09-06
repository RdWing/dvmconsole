// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Settings;

internal static class CrossProcessStoreLock
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    public static FileStream Acquire(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        string? parent = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(parent))
            AppDataFileProtection.EnsureDirectory(parent);

        long started = Environment.TickCount64;
        IOException? lastFailure = null;
        while (Environment.TickCount64 - started < Timeout.TotalMilliseconds)
        {
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                AppDataFileProtection.EnsureFile(lockPath);
                return stream;
            }
            catch (IOException exception)
            {
                lastFailure = exception;
                Thread.Sleep(RetryDelay);
            }
        }

        throw new IOException(
            $"Timed out waiting for another DVM Console process to finish writing '{lockPath}'.",
            lastFailure);
    }
}

public sealed class SettingsConflictException(string message) : IOException(message);
