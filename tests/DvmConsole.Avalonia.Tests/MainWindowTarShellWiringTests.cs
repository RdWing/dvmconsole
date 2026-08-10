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
    /// RED contract for shell TAR plumbing: the MainWindow constructor exposes
    /// the persistence adapter without disturbing existing optional parameters.
    /// App composition is verified by the production wiring review and the
    /// existing MainWindowViewModel composition contract.
    /// </summary>
    public sealed class MainWindowTarShellWiringTests
    {
        [Fact]
        public void MainWindowConstructor_ExposesTarPersistenceAfterExistingOptionalArguments()
        {
            ConstructorInfo constructor = Assert.Single(
                typeof(MainWindow)
                    .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => candidate.GetParameters().Any(
                        parameter => parameter.ParameterType == typeof(AudioSettingsPersistence))
                        && candidate.GetParameters().Any(
                            parameter => parameter.ParameterType == typeof(AliasResolver))));
            ParameterInfo[] parameters = constructor.GetParameters();

            Assert.Equal(typeof(AliasResolver), parameters[^2].ParameterType);
            Assert.Equal(typeof(TarSettingsPersistence), parameters[^1].ParameterType);
            Assert.True(parameters[^1].IsOptional);
            Assert.Null(parameters[^1].DefaultValue);
        }
    }
}
