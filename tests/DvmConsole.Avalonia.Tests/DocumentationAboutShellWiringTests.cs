// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Reflection;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Avalonia.Views;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Gate 7.4 RED contracts for packaged documentation access and the
    /// enriched About surface. The external opener is intentionally tested
    /// through shell source/assembly contracts; launching a host browser is
    /// a macOS acceptance concern, not a Linux headless test.
    /// </summary>
    public sealed class DocumentationAboutShellWiringTests
    {
        [Fact]
        public void AboutViewModelExposesRuntimeNativeReadinessAndDocumentation()
        {
            var type = typeof(AboutWindowViewModel);
            var vm = new AboutWindowViewModel(
                "Digital Voice Modem",
                "Desktop Dispatch Console",
                Version.Parse("1.2.0.0"),
                "R01A02 (abcdef123456789)");

            PropertyInfo? runtime = type.GetProperty("RuntimeLine");
            PropertyInfo? native = type.GetProperty("NativeReadinessLine");
            PropertyInfo? documentation = type.GetProperty("DocumentationUrl");

            Assert.NotNull(runtime);
            Assert.NotNull(native);
            Assert.NotNull(documentation);
            Assert.False(string.IsNullOrWhiteSpace(runtime!.GetValue(vm) as string));
            Assert.False(string.IsNullOrWhiteSpace(native!.GetValue(vm) as string));
            Assert.Equal(
                "https://github.com/DVMProject/dvmconsole/tree/r01a02_dev/dvmconsole/Docs",
                documentation!.GetValue(vm));
        }

        [Fact]
        public void AboutWindowRendersRuntimeAndNativeReadiness()
        {
            string xaml = FileText("DvmConsole.Avalonia/Views/AboutWindow.axaml");
            string codeBehind = FileText("DvmConsole.Avalonia/Views/AboutWindow.axaml.cs");
            var type = typeof(AboutWindow);

            Assert.Contains("Text=\"{Binding RuntimeLine}\"", xaml);
            Assert.Contains("Text=\"{Binding NativeReadinessLine}\"", xaml);
            Assert.Contains("Click=\"Documentation_OnClick\"", xaml);
            Assert.Contains("OpenUrl(viewModel.DocumentationUrl)", codeBehind);
            Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
            Assert.NotNull(type.GetConstructor(new[] { typeof(string) }));
        }

        [Fact]
        public void AboutWindowForwardsNativeReadinessIntoViewModel()
        {
            string codeBehind = FileText("DvmConsole.Avalonia/Views/AboutWindow.axaml.cs");
            MethodInfo? factory = typeof(AboutWindow).GetMethod(
                "CreateViewModel",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.Contains("DataContext = CreateViewModel(nativeReadiness);", codeBehind);
            Assert.NotNull(factory);

            var viewModel = Assert.IsType<AboutWindowViewModel>(
                factory!.Invoke(null, new object?[] { "Vocoder ready" }));
            Assert.Equal("Vocoder ready", viewModel.NativeReadinessLine);
        }

        [Fact]
        public void HelpMenuKeepsDebugLogsAndAddsPackagedDocumentationOpener()
        {
            string appSource = FileText("DvmConsole.Avalonia/App.axaml.cs");

            Assert.Contains("CreateDebugLogMenuItem", appSource);
            Assert.Contains("CreateDocumentationMenuItem", appSource);
            Assert.Contains("AboutWindowViewModel.DocumentationLink", appSource);
            Assert.Contains(
                "item.Click += (_, _) => OpenExternalUrl(AboutWindowViewModel.DocumentationLink);",
                appSource);
            Assert.Contains(
                "&& string.Equals(header, \"Documentation\", StringComparison.Ordinal)",
                appSource);
            Assert.Contains("documentationMenu.Items.Add(documentationItem)", appSource);
            Assert.Contains("!documentationMenu.Items.OfType<NativeMenuItem>().Any", appSource);
            int debugIndex = appSource.IndexOf(
                "helpMenu.Items.Add(debugLogItem)",
                StringComparison.Ordinal);
            int documentationIndex = appSource.IndexOf(
                "documentationMenu.Items.Add(documentationItem)",
                StringComparison.Ordinal);
            Assert.True(debugIndex >= 0, "The Help menu must retain Debug Logs insertion.");
            Assert.True(
                documentationIndex >= 0,
                "The Help menu must insert the Documentation item.");
            Assert.True(
                debugIndex < documentationIndex,
                "Debug Logs must remain before Documentation in Help.");
            Assert.Contains("UseShellExecute = true", appSource);
        }

        [Fact]
        public void MacOsDocumentationExplainsBuildBundlePermissionsAndRuntime()
        {
            string readme = FileText("README.md");
            string building = FileText("dvmconsole/Docs/Getting Started/02-Building.md");
            string matrix = FileText("dvmconsole/Docs/Porting/macOS Feature Matrix.md");

            Assert.Contains("DvmConsole.Avalonia", readme);
            Assert.Contains("osx-arm64", readme);
            Assert.Contains("osx-x64", readme);
            Assert.Contains("build-app.sh", readme);
            Assert.Contains("build-vocoder.sh", readme);
            Assert.Contains("libvocoder.dylib", readme);
            Assert.Contains("unsigned", readme, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unnotarized", readme, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TCC", readme);
            Assert.Contains("Accessibility", building);
            Assert.Contains("Input Monitoring", building);
            Assert.Contains("Microphone", building);
            Assert.Contains("osx-arm64", building);
            Assert.Contains("osx-x64", building);
            Assert.Contains("build-app.sh", building);
            Assert.Contains("build-vocoder.sh", building);
            Assert.Contains("libvocoder.dylib", building);
            Assert.Contains("unnotarized", building, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Application Support", building);
            Assert.Contains("Environment.CurrentDirectory/configs/codeplug.yml", building);
            Assert.Contains("AliasPath", building);
            Assert.Contains("in-memory", building, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("UserSettings.json", readme);
            Assert.Contains("Environment.CurrentDirectory/configs/codeplug.yml", readme);
            Assert.Contains("Codeplug.System.AliasPath", readme);
            Assert.Contains("in-memory", readme, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Known limitations", matrix);
            Assert.Contains("UNVERIFIED", matrix);
            Assert.Contains("Debug Logs", matrix);
            Assert.Contains("osx-x64", matrix);
            Assert.Contains("osx-arm64", matrix);
            Assert.Contains("build-app.sh", matrix);
            Assert.Contains("build-vocoder.sh", matrix);
            Assert.Contains("libvocoder.dylib", matrix);
            Assert.Contains("unsigned", matrix, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unnotarized", matrix, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("browser", matrix, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TCC", matrix);
            Assert.Contains("CoreAudio", matrix);
            Assert.Contains("FNE", matrix);
            Assert.Contains("signing", matrix, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Environment.CurrentDirectory/configs/codeplug.yml", matrix);
            Assert.Contains("in-memory", matrix, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "settings, aliases, diagnostics and the default `codeplug.yml` are resolved there",
                building,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "settings, aliases, diagnostics and the default `codeplug.yml` are resolved there",
                matrix,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string FileText(string relativePath)
            => File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
