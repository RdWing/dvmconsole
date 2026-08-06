// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the codeplug-loader seeding slice: the systems
* produced by the headless Core loader must flow into the Avalonia
* shell's FNE connection manager, and the MainWindow full-arity
* constructor must accept a systems list (trailing optional param) so
* real codeplug rows appear without any XAML change.
*
* Locked here: MainWindowViewModel(systems, ...) seeds
* FneConnections.Systems with real row projections (Name/Address/Port);
* a failed/missing load degrades to empty systems (HasNoSystems) with
* no crash.
*/
using System.Collections.Generic;
using dvmconsole;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for codeplug seeding into the Avalonia shell.
    /// </summary>
    public sealed class CodeplugLoaderSeedingTests
    {
        private const string MinimalCodeplugYaml = @"systems:
  - name: Simplex Repeater
    identity: 310001
    address: 127.0.0.1
    port: 31000
    password: placeholder-password
    encrypted: false
    peerId: 310000
    rid: ""3100001""
";

        [Fact]
        public void LoadedSystems_SeedFneConnectionRows()
        {
            var result = CodeplugLoader.LoadFromText(MinimalCodeplugYaml);
            Assert.True(result.Succeeded);

            var viewModel = new MainWindowViewModel(
                result.Codeplug!.Systems, null, null, null, null);

            Assert.True(viewModel.FneConnections.HasSystems);
            var row = Assert.Single(viewModel.FneConnections.Systems);
            Assert.Equal("Simplex Repeater", row.SystemName);
            Assert.Equal("127.0.0.1", row.Address);
            Assert.Equal(31000, row.Port);
            Assert.Equal("127.0.0.1:31000", row.Endpoint);
        }

        [Fact]
        public void MissingCodeplug_EmptySystems_NoCrash()
        {
            var result = CodeplugLoader.LoadFromFile("/nonexistent/codeplug.yml");
            Assert.True(result.FileMissing);

            var viewModel = new MainWindowViewModel(
                result.Codeplug?.Systems, null, null, null, null);

            Assert.True(viewModel.FneConnections.HasNoSystems);
            Assert.False(viewModel.FneConnections.HasSystems);
        }

        [Fact]
        public void MalformedCodeplug_EmptySystems_NoCrash()
        {
            var result = CodeplugLoader.LoadFromText("systems: [unclosed");
            Assert.False(result.Succeeded);

            var viewModel = new MainWindowViewModel(
                result.Codeplug?.Systems, null, null, null, null);

            Assert.True(viewModel.FneConnections.HasNoSystems);
        }
    }
}
