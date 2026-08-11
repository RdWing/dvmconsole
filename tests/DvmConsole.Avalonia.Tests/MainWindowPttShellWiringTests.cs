// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Linq;
using System.Reflection;
using dvmconsole;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.Services;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for shell PTT plumbing: the full MainWindow constructor
    /// forwards the already-composed PTT persistence adapter after TAR while
    /// preserving all earlier optional parameters. App shared-store wiring is
    /// kept in the production shell review because startup construction has
    /// native and dispatcher side effects.
    /// </summary>
    public sealed class MainWindowPttShellWiringTests
    {
        [Fact]
        public void MainWindowConstructor_ExposesPttPersistenceAfterTarPersistence()
        {
            ConstructorInfo constructor = Assert.Single(
                typeof(MainWindow)
                    .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => candidate.GetParameters().Any(
                        parameter => parameter.ParameterType == typeof(AudioSettingsPersistence))
                        && candidate.GetParameters().Any(
                            parameter => parameter.ParameterType == typeof(AliasResolver))));
            ParameterInfo[] parameters = constructor.GetParameters();

            Assert.Equal(typeof(TarSettingsPersistence), parameters[^2].ParameterType);
            Assert.Equal(typeof(PttSettingsPersistence), parameters[^1].ParameterType);
            Assert.Equal(typeof(AliasResolver), parameters[^3].ParameterType);
            Assert.True(parameters[^2].IsOptional);
            Assert.Null(parameters[^2].DefaultValue);
            Assert.True(parameters[^1].IsOptional);
            Assert.Null(parameters[^1].DefaultValue);
        }
    }
}
