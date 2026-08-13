// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Owns the failure-safe ordering for replacing the active codeplug.
    /// Loading and validation happen before the current runtime is stopped;
    /// only a successful load may enter the stop-and-apply phase.
    /// Runtime construction and shell status presentation remain injected
    /// boundaries.
    /// </summary>
    public sealed class CodeplugReloadCoordinator
    {
        private readonly Func<string, CodeplugLoadResult> load;
        private readonly Func<Task> stopCurrentRuntime;
        private readonly Func<Codeplug, Task> applyCodeplug;
        private readonly Action<string>? reportStatus;
        private readonly Func<Codeplug, Task>? prepareCodeplug;
        private readonly Func<Task>? discardPreparedCodeplug;

        public CodeplugReloadCoordinator(
            Func<string, CodeplugLoadResult> load,
            Func<Task> stopCurrentRuntime,
            Func<Codeplug, Task> applyCodeplug,
            Action<string>? reportStatus = null,
            Func<Codeplug, Task>? prepareCodeplug = null,
            Func<Task>? discardPreparedCodeplug = null)
        {
            this.load = load ?? throw new ArgumentNullException(nameof(load));
            this.stopCurrentRuntime = stopCurrentRuntime
                ?? throw new ArgumentNullException(nameof(stopCurrentRuntime));
            this.applyCodeplug = applyCodeplug
                ?? throw new ArgumentNullException(nameof(applyCodeplug));
            this.reportStatus = reportStatus;
            this.prepareCodeplug = prepareCodeplug;
            this.discardPreparedCodeplug = discardPreparedCodeplug;
        }

        /// <summary>
        /// Loads a candidate codeplug before touching the active runtime.
        /// Failed or cancelled loads leave the current runtime untouched.
        /// A successful load stops the current runtime once and applies the
        /// parsed codeplug once.
        /// </summary>
        public async Task<bool> ReloadAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CodeplugLoadResult result = load(filePath);
            if (!result.Succeeded || result.Codeplug is null)
            {
                reportStatus?.Invoke(
                    "Codeplug reload failed: "
                    + (result.ErrorMessage ?? "load failed"));
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (prepareCodeplug is not null)
                {
                    await prepareCodeplug(result.Codeplug);
                }

                await stopCurrentRuntime();
                await applyCodeplug(result.Codeplug);
                reportStatus?.Invoke("Codeplug reloaded.");
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DiscardPreparedCodeplugAsync();
                reportStatus?.Invoke("Codeplug reload cancelled.");
                return false;
            }
            catch (Exception exception)
            {
                await DiscardPreparedCodeplugAsync();
                reportStatus?.Invoke(
                    "Codeplug reload failed: " + exception.Message);
                return false;
            }
        }

        private async Task DiscardPreparedCodeplugAsync()
        {
            if (discardPreparedCodeplug is null)
            {
                return;
            }

            try
            {
                await discardPreparedCodeplug();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Prepared codeplug cleanup failed: {exception}");
            }
        }
    }
}
