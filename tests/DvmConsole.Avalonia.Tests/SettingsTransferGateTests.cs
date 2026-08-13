// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the headless Gate 7.2 settings-transfer slice.
    /// File dialogs, confirmation UI, and live-window composition remain shell
    /// work; this contract locks category selection, portable JSON, isolation,
    /// atomic mutation, one runtime refresh, and explicit secret exclusion.
    /// </summary>
    public sealed class SettingsTransferGateTests
    {
        private sealed class TemporaryDirectory : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-settings-transfer-gate-" + Guid.NewGuid().ToString("N"));

            public TemporaryDirectory()
            {
                Directory.CreateDirectory(Root);
            }

            public string PathFor(string name) => Path.Combine(Root, name);

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Root, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup for hermetic test fixtures.
                }
            }
        }

        [Fact]
        public void CategorySelection_SelectAllAndNoneAreChangeOnly()
        {
            using var temp = new TemporaryDirectory();
            var viewModel = new SettingsTransferViewModel(
                new SettingsTransferService(temp.PathFor("UserSettings.json")));

            Assert.NotEmpty(viewModel.Categories);
            Assert.All(viewModel.Categories, category => Assert.True(category.IsSelected));

            viewModel.SelectNone();
            Assert.All(viewModel.Categories, category => Assert.False(category.IsSelected));

            viewModel.SelectAll();
            Assert.All(viewModel.Categories, category => Assert.True(category.IsSelected));
        }

        [Fact]
        public async Task ExportSelectedCategories_WritesPortableJsonAndOmitsUnselectedAndSecretFields()
        {
            using var temp = new TemporaryDirectory();
            string settingsPath = temp.PathFor("UserSettings.json");
            File.WriteAllText(
                settingsPath,
                "{\"AudioInputDeviceKey\":\"mic-a\",\"TalkPermitTone\":true,\"FnePassword\":\"must-not-export\",\"UnknownField\":42}");

            var service = new SettingsTransferService(settingsPath);
            var viewModel = new SettingsTransferViewModel(service);
            viewModel.SelectNone();
            viewModel.FindCategory("audio")!.IsSelected = true;
            string exportPath = temp.PathFor("nested/export.json");

            bool succeeded = await viewModel.ExportAsync(exportPath);

            Assert.True(succeeded);
            JObject transfer = JObject.Parse(File.ReadAllText(exportPath));
            Assert.Equal("dvmconsole-settings-transfer", (string?)transfer["Format"]);
            Assert.Equal(new[] { "audio" }, transfer["Categories"]!.Values<string>());
            JObject settings = (JObject)transfer["Settings"]!;
            Assert.Equal("mic-a", (string?)settings["AudioInputDeviceKey"]);
            Assert.Null(settings["TalkPermitTone"]);
            Assert.Null(settings["FnePassword"]);
            Assert.Null(settings["UnknownField"]);
        }

        [Fact]
        public async Task ImportSelectedCategories_IsolatesCategoriesAndReloadsExactlyOnce()
        {
            using var temp = new TemporaryDirectory();
            string sourceSettingsPath = temp.PathFor("source.json");
            string targetSettingsPath = temp.PathFor("target.json");
            string transferPath = temp.PathFor("transfer.json");
            File.WriteAllText(
                sourceSettingsPath,
                "{\"AudioInputDeviceKey\":\"imported-mic\",\"TalkPermitTone\":true,\"UnknownField\":7}");
            File.WriteAllText(
                targetSettingsPath,
                "{\"AudioInputDeviceKey\":\"old-mic\",\"TalkPermitTone\":false,\"KeepMe\":{\"value\":1}}");

            var sourceService = new SettingsTransferService(sourceSettingsPath);
            var sourceViewModel = new SettingsTransferViewModel(sourceService);
            sourceViewModel.SelectNone();
            sourceViewModel.FindCategory("audio")!.IsSelected = true;
            Assert.True(await sourceViewModel.ExportAsync(transferPath));

            var targetService = new SettingsTransferService(targetSettingsPath);
            var targetViewModel = new SettingsTransferViewModel(targetService);
            targetViewModel.SelectNone();
            targetViewModel.FindCategory("audio")!.IsSelected = true;
            var reloadCalls = 0;

            bool succeeded = await targetViewModel.ImportAsync(
                transferPath,
                confirmAsync: () => Task.FromResult(true),
                reloadRuntimeAsync: () =>
                {
                    reloadCalls++;
                    return Task.CompletedTask;
                });

            Assert.True(succeeded);
            Assert.Equal(1, reloadCalls);
            JObject settings = JObject.Parse(File.ReadAllText(targetSettingsPath));
            Assert.Equal("imported-mic", (string?)settings["AudioInputDeviceKey"]);
            Assert.False((bool)settings["TalkPermitTone"]!);
            Assert.Equal(1, (int)settings["KeepMe"]!["value"]!);
        }

        [Fact]
        public async Task FailedImportLeavesSettingsUntouchedAndDoesNotReload()
        {
            using var temp = new TemporaryDirectory();
            string settingsPath = temp.PathFor("UserSettings.json");
            string importPath = temp.PathFor("broken.json");
            string original = "{\"AudioInputDeviceKey\":\"old-mic\",\"KeepMe\":true}";
            File.WriteAllText(settingsPath, original);
            File.WriteAllText(importPath, "{ definitely not json");

            var viewModel = new SettingsTransferViewModel(
                new SettingsTransferService(settingsPath));
            viewModel.SelectNone();
            viewModel.FindCategory("audio")!.IsSelected = true;
            var reloadCalls = 0;

            bool succeeded = await viewModel.ImportAsync(
                importPath,
                confirmAsync: () => Task.FromResult(true),
                reloadRuntimeAsync: ()
                    =>
                    {
                        reloadCalls++;
                        return Task.CompletedTask;
                    });

            Assert.False(succeeded);
            Assert.Equal(0, reloadCalls);
            Assert.Equal(original, File.ReadAllText(settingsPath));
        }

        [Fact]
        public async Task ImportAndResetRequireExplicitConfirmation()
        {
            using var temp = new TemporaryDirectory();
            string settingsPath = temp.PathFor("UserSettings.json");
            File.WriteAllText(settingsPath, "{\"KeepMe\":true}");
            var viewModel = new SettingsTransferViewModel(
                new SettingsTransferService(settingsPath));

            Assert.False(await viewModel.ResetAsync(() => Task.FromResult(false)));
            Assert.True(File.Exists(settingsPath));

            Assert.True(await viewModel.ResetAsync(() => Task.FromResult(true)));
            Assert.False(File.Exists(settingsPath));
        }

        [Fact]
        public void CategoryDefinitionsContainNoSecretMaterialNames()
        {
            using var temp = new TemporaryDirectory();
            var viewModel = new SettingsTransferViewModel(
                new SettingsTransferService(temp.PathFor("UserSettings.json")));
            string[] propertyNames = viewModel.Categories
                .SelectMany(category => category.PropertyNames)
                .ToArray();

            Assert.DoesNotContain(propertyNames, name =>
                string.Equals(name, "FnePassword", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Password", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "KeyMaterial", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "EncryptionKey", StringComparison.OrdinalIgnoreCase));
        }
    }
}
