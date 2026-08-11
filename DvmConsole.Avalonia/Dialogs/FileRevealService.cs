// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Avalonia.Dialogs
{
    /// <summary>Reveals an existing file in the host file manager.</summary>
    public interface IFileRevealService
    {
        Task<bool> RevealAsync(string filePath, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Cross-platform desktop-file-manager adapter. It reports false for invalid
    /// paths or launch failures instead of throwing into a click handler.
    /// </summary>
    public sealed class DesktopFileRevealService : IFileRevealService
    {
        public Task<bool> RevealAsync(string filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return Task.FromResult(false);

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ProcessStartInfo startInfo = OperatingSystem.IsMacOS()
                    ? CreateStartInfo("open", "-R", filePath)
                    : OperatingSystem.IsWindows()
                        ? CreateStartInfo("explorer.exe", "/select," + filePath)
                        : CreateStartInfo("xdg-open", Path.GetDirectoryName(filePath) ?? filePath);
                using Process? process = Process.Start(startInfo);
                return Task.FromResult(process is not null);
            }
            catch (Exception)
            {
                return Task.FromResult(false);
            }
        }

        private static ProcessStartInfo CreateStartInfo(string fileName, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
            return startInfo;
        }
    }

    /// <summary>No-op fallback used by headless or unsupported hosts.</summary>
    public sealed class NoopFileRevealService : IFileRevealService
    {
        public static NoopFileRevealService Instance { get; } = new NoopFileRevealService();

        private NoopFileRevealService()
        {
        }

        public Task<bool> RevealAsync(string filePath, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
