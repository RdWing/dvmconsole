// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Linq;
using System.Reflection;
using dvmconsole;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Regression contract for shell TAR/PTT plumbing: the MainWindow
    /// constructor exposes TAR/PTT before the new trailing viewer dependencies without
    /// disturbing earlier optional parameters.
    /// </summary>
    public sealed class MainWindowTarShellWiringTests
    {
        [Fact]
        public void MainWindowConstructor_AppendsViewerDependenciesAfterExistingOptionalArguments()
        {
            ConstructorInfo constructor = Assert.Single(
                typeof(MainWindow)
                    .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => candidate.GetParameters().Any(
                        parameter => parameter.ParameterType == typeof(AudioSettingsPersistence))
                        && candidate.GetParameters().Any(
                            parameter => parameter.ParameterType == typeof(AliasResolver))));
            ParameterInfo[] parameters = constructor.GetParameters();

            Assert.Equal(typeof(AliasResolver), parameters[^5].ParameterType);
            Assert.Equal(typeof(TarSettingsPersistence), parameters[^4].ParameterType);
            Assert.Equal(typeof(PttSettingsPersistence), parameters[^3].ParameterType);
            Assert.Equal(typeof(TarRecorder), parameters[^2].ParameterType);
            Assert.Equal(typeof(IAudioWaveFilePlayer), parameters[^1].ParameterType);
            Assert.True(parameters[^4].IsOptional);
            Assert.Null(parameters[^4].DefaultValue);
            Assert.True(parameters[^3].IsOptional);
            Assert.Null(parameters[^3].DefaultValue);
            Assert.True(parameters[^2].IsOptional);
            Assert.Null(parameters[^2].DefaultValue);
            Assert.True(parameters[^1].IsOptional);
            Assert.Null(parameters[^1].DefaultValue);
        }
    }
}
