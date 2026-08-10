// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Reflection;
using Avalonia.Controls;
using DvmConsole.Avalonia;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for native TAR menu wiring. The menu factory is pure
    /// enough to test without a desktop lifetime; the MainWindow entry point
    /// owns dialog construction/owner assignment and must guard missing TAR
    /// composition.
    /// </summary>
    public sealed class TarMenuWiringTests
    {
        [Fact]
        public void AppCreatesNamedTarConfigurationMenuItem()
        {
            NativeMenuItem item = App.CreateTarConfigurationMenuItem(null!);

            Assert.Equal("TAR Configuration", item.Header);
        }

        [Fact]
        public void MainWindowExposesTarConfigurationEntryPoint()
        {
            MethodInfo? method = typeof(MainWindow).GetMethod(
                "OpenTarConfiguration",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);
            Assert.Empty(method!.GetParameters());
            Assert.Equal(typeof(void), method.ReturnType);
        }
    }
}
