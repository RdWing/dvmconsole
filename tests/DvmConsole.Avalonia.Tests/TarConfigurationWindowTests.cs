// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Linq;
using System.Reflection;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Avalonia.Views;
using DvmConsole.Platform.Dialogs;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the TAR configuration dialog boundary. The dialog
    /// owns only presentation and folder-picker invocation; the headless VM
    /// remains the source of validation and save payloads.
    /// </summary>
    public sealed class TarConfigurationWindowTests
    {
        [Fact]
        public void Constructor_RequiresViewModelAndInjectedFileDialogService()
        {
            ConstructorInfo constructor = Assert.Single(
                typeof(TarConfigurationWindow).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            ParameterInfo[] parameters = constructor.GetParameters();

            Assert.Equal(2, parameters.Length);
            Assert.Equal(typeof(TarConfigurationViewModel), parameters[0].ParameterType);
            Assert.Equal(typeof(IFileDialogService), parameters[1].ParameterType);
        }
    }
}
