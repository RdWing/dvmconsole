// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DesktopConfigurationStoreContextTests
{
    [Fact]
    public async Task AsyncCreationDoesNotRunBlockingInitializationOnTheCaller()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-store-context-tests",
            Guid.NewGuid().ToString("N"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var safetyRelease = new Timer(
            _ => release.Set(),
            null,
            TimeSpan.FromMilliseconds(500),
            Timeout.InfiniteTimeSpan);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Task<DesktopConfigurationStoreContext> creation = DesktopConfigurationStoreContext.CreateAsync(
                root,
                factory: path =>
                {
                    entered.Set();
                    release.Wait();
                    return DesktopConfigurationStoreContext.Create(path);
                });
            stopwatch.Stop();

            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(creation.IsCompleted);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(200));

            release.Set();
            DesktopConfigurationStoreContext context = await creation;
            Assert.NotNull(context.Library);
            Assert.NotNull(context.Materializer);
        }
        finally
        {
            release.Set();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
