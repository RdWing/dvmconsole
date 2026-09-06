// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

/// <summary>
/// A process-held lease, not a PID marker: the OS releases ownership on crash.
/// The lock file stays in place so a new owner cannot lock a different inode.
/// </summary>
internal sealed class RecordingRootLease(FileStream stream) : IDisposable
{
    public static RecordingRootLease? TryAcquire(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 1
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return new RecordingRootLease(new FileStream(Path.Combine(root, ".recording-owner.lock"), options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => stream.Dispose();
}
