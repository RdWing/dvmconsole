// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class SettingsTransferRuntimeRefreshTests
    {
        [Fact]
        public async Task ResetAsync_UsesTheSingleInjectedReloadCallback()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-settings-reset-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{\"KeepMe\":true}");
                var viewModel = new SettingsTransferViewModel(new SettingsTransferService(path));
                var reloadCalls = 0;

                bool succeeded = await viewModel.ResetAsync(
                    () => Task.FromResult(true),
                    () =>
                    {
                        reloadCalls++;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);

                Assert.True(succeeded);
                Assert.False(File.Exists(path));
                Assert.Equal(1, reloadCalls);

                await viewModel.ImportAsync(
                    path,
                    () => Task.FromResult(false),
                    () =>
                    {
                        reloadCalls++;
                        return Task.CompletedTask;
                    });
                Assert.Equal(1, reloadCalls);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void MainWindowSettingsRefreshSuppressesStaleDisposePersistence()
        {
            string source = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "../../../../../DvmConsole.Avalonia/MainWindow.axaml.cs"));

            int enableIndex = source.IndexOf(
                "suppressRuntimeSettingsPersistence = true",
                StringComparison.Ordinal);
            int reloadIndex = source.IndexOf(
                "await codeplugReloadCoordinator.ReloadAsync",
                Math.Max(0, enableIndex),
                StringComparison.Ordinal);
            int clearIndex = source.IndexOf(
                "suppressRuntimeSettingsPersistence = previous",
                Math.Max(0, reloadIndex),
                StringComparison.Ordinal);

            Assert.True(enableIndex >= 0);
            Assert.True(reloadIndex > enableIndex);
            Assert.True(clearIndex > reloadIndex);

            int saveWebStreamsIndex = source.IndexOf(
                "SaveWebStreamSettings();",
                StringComparison.Ordinal);
            int saveLayoutIndex = source.IndexOf(
                "SaveLayoutSettings();",
                Math.Max(0, saveWebStreamsIndex),
                StringComparison.Ordinal);
            int guardIndex = source.IndexOf(
                "if (!suppressRuntimeSettingsPersistence)",
                Math.Max(0, saveWebStreamsIndex),
                StringComparison.Ordinal);

            Assert.True(saveWebStreamsIndex >= 0);
            Assert.True(saveLayoutIndex > saveWebStreamsIndex);
            Assert.True(guardIndex < saveWebStreamsIndex);
        }
    }
}
