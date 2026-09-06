// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Xml.Linq;
using YamlDotNet.RepresentationModel;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void WorkflowDeclaresAllTargetsAndRequiresSharedSourceValidation()
    {
        var jobs = ReadWorkflowJobs();
        var build = (YamlMappingNode)jobs["test-and-publish"];
        Assert.Equal("source-validation", build["needs"].ToString());
        Assert.True(jobs.Children.ContainsKey(new YamlScalarNode("source-validation")));
        var strategy = (YamlMappingNode)build["strategy"];
        var matrix = (YamlMappingNode)strategy["matrix"];
        var targets = ((YamlSequenceNode)matrix["include"]).Children.Cast<YamlMappingNode>()
            .Select(row => row["rid"].ToString()).Order().ToArray();
        Assert.Equal(new[] { "linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64" }, targets);
        var commands = ((YamlSequenceNode)build["steps"]).Children.Cast<YamlMappingNode>()
            .Where(step => step.Children.ContainsKey(new YamlScalarNode("run")))
            .Select(step => step["run"].ToString()).ToArray();
        Assert.Contains("python -m unittest discover -s scripts/tests -p 'test_*.py'", commands);
        var publisher = (YamlMappingNode)jobs["publish-release"];
        var dependencies = ((YamlSequenceNode)publisher["needs"]).Children.Select(node => node.ToString()).ToArray();
        Assert.Contains("test-and-publish", dependencies);
        Assert.Contains("dependency-advisories", dependencies);
    }

    [Fact]
    public void WorkflowGrantsAttestationPermissionsOnlyToTheTaggedPublisher()
    {
        foreach (var entry in ReadWorkflowJobs().Children)
        {
            var job = (YamlMappingNode)entry.Value;
            if (entry.Key.ToString() == "publish-release")
            {
                Assert.Equal("startsWith(github.ref, 'refs/tags/v')", job["if"].ToString());
                var permissions = (YamlMappingNode)job["permissions"];
                Assert.Equal("write", permissions["id-token"].ToString());
                Assert.Equal("write", permissions["attestations"].ToString());
            }
            else if (job.Children.TryGetValue(new YamlScalarNode("permissions"), out var node))
            {
                var permissions = Assert.IsType<YamlMappingNode>(node);
                foreach (string key in new[] { "id-token", "attestations" })
                    Assert.False(permissions.Children.TryGetValue(new YamlScalarNode(key), out var value) && value.ToString() == "write");
            }
        }
    }

    private static YamlMappingNode ReadWorkflowJobs()
    {
        string root = Directory.GetParent(FindSourceRoot())!.FullName;
        using var reader = File.OpenText(Path.Combine(root, ".github", "workflows", "build.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var workflow = (YamlMappingNode)yaml.Documents.Single().RootNode;
        var defaults = (YamlMappingNode)workflow["permissions"];
        Assert.Equal("read", defaults["contents"].ToString());
        foreach (string key in new[] { "id-token", "attestations" })
            Assert.False(defaults.Children.TryGetValue(new YamlScalarNode(key), out var value) && value.ToString() == "write");
        return (YamlMappingNode)workflow["jobs"];
    }

    [Fact]
    public void ReceiveAndTransmitControllersDependOnlyOnNarrowPorts()
    {
        Type[] controllers =
        [
            typeof(ReceiveOutputController),
            typeof(TransmitAudioTransitionController)
        ];

        foreach (Type controller in controllers)
        {
            Type[] dependencies = Assert.Single(controller.GetConstructors()).GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray();
            Assert.All(dependencies, dependency => Assert.True(
                dependency.IsInterface && dependency.Name.StartsWith('I'),
                $"{controller.Name} depends directly on {dependency.Name}."));
            Assert.InRange(dependencies.Length, 1, 5);
        }
    }

    [Fact]
    public void ExtractedSessionControllersDoNotExposeDelegateParameterBags()
    {
        Type[] controllers =
        [
            typeof(ConnectionSessionController),
            typeof(PatchRoutingController),
            typeof(ReceivePresentationController),
            typeof(ReceiveSessionController),
            typeof(RecordingCommandController),
            typeof(ShellLayoutController),
            typeof(ShellSettingsController),
            typeof(ConfigurationStudioEntityEditController)
        ];

        foreach (Type controller in controllers)
        {
            Type[] dependencies = Assert.Single(controller.GetConstructors()).GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray();
            Assert.DoesNotContain(
                dependencies,
                dependency => typeof(Delegate).IsAssignableFrom(dependency));
        }
    }

    [Fact]
    public void SessionCompositionProvidesTheRequiredControllerTypes()
    {
        string[] controllerNames =
        [
            nameof(AudioInputSettingsController),
            nameof(ConnectionSessionController),
            nameof(HistoryRecordingController),
            nameof(OperatorUndoController),
            nameof(PatchRoutingController),
            nameof(PttSessionController),
            nameof(ReceiveOutputController),
            nameof(ReceivePresentationController),
            nameof(ReceiveSessionController),
            nameof(RecordingCommandController),
            nameof(RuntimeHealthController),
            nameof(ShellLayoutController),
            nameof(ShellSettingsController),
            nameof(TonePresentationController),
            nameof(TransmitAudioTransitionController),
            nameof(WebStreamPlaybackCoordinator)
        ];

        string[] providedTypes = typeof(MainWindowSessionComposition)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(method => method.ReturnType.Name)
            .ToArray();
        Assert.All(controllerNames, name => Assert.Contains(name, providedTypes));
    }

    [Fact]
    public void ProductionProjectsKeepTheEstablishedDependencyDirection()
    {
        string sourceRoot = FindSourceRoot();
        var expected = new Dictionary<string, string[]>
        {
            ["DvmConsole.CodeplugValidator"] = ["DvmConsole.Core"],
            ["DvmConsole.Application"] =
                ["DvmConsole.Audio.Abstractions", "DvmConsole.Audio.Core", "DvmConsole.Core", "DvmConsole.Media", "DvmConsole.Operations", "DvmConsole.Ptt.Abstractions", "DvmConsole.Vocoder.Abstractions"],
            ["DvmConsole.Audio.Abstractions"] = [],
            ["DvmConsole.Audio.Core"] = ["DvmConsole.Audio.Abstractions"],
            ["DvmConsole.Audio.Desktop"] =
                ["DvmConsole.Audio.Abstractions", "DvmConsole.Audio.Core", "DvmConsole.Audio.Linux", "DvmConsole.Audio.MacOS", "DvmConsole.Audio.Windows"],
            ["DvmConsole.Audio.Linux"] = ["DvmConsole.Audio.Abstractions", "DvmConsole.Audio.Core"],
            ["DvmConsole.Audio.MacOS"] = ["DvmConsole.Audio.Abstractions", "DvmConsole.Audio.Core"],
            ["DvmConsole.Audio.Windows"] = ["DvmConsole.Audio.Abstractions"],
            ["DvmConsole.Configuration.Yaml"] = ["DvmConsole.Application", "DvmConsole.Core"],
            ["DvmConsole.Core"] = [],
            ["DvmConsole.Desktop"] =
            [
                "DvmConsole.Application",
                "DvmConsole.Audio.Abstractions",
                "DvmConsole.Audio.Core",
                "DvmConsole.Audio.Desktop",
                "DvmConsole.Configuration.Yaml",
                "DvmConsole.Core",
                "DvmConsole.FneClient",
                "DvmConsole.Media",
                "DvmConsole.Operations",
                "DvmConsole.Presentation",
                "DvmConsole.Ptt.Abstractions",
                "DvmConsole.Ptt.Desktop",
                "DvmConsole.Storage",
                "DvmConsole.Vocoder.Abstractions",
                "DvmConsole.Vocoder.Native"
            ],
            ["DvmConsole.Fne"] = [],
            ["DvmConsole.FneClient"] = ["DvmConsole.Core", "DvmConsole.Fne"],
            ["DvmConsole.Media"] =
                ["DvmConsole.Audio.Abstractions", "DvmConsole.Audio.Core", "DvmConsole.Core", "DvmConsole.Fne", "DvmConsole.Vocoder.Abstractions"],
            ["DvmConsole.Operations"] = ["DvmConsole.Core"],
            ["DvmConsole.Presentation"] = ["DvmConsole.Application"],
            ["DvmConsole.Ptt.Abstractions"] = [],
            ["DvmConsole.Ptt.Desktop"] = ["DvmConsole.Ptt.Abstractions"],
            ["DvmConsole.Storage"] = ["DvmConsole.Application", "DvmConsole.Core"],
            ["DvmConsole.Vocoder.Abstractions"] = [],
            ["DvmConsole.Vocoder.Native"] = ["DvmConsole.Vocoder.Abstractions"]
        };

        foreach ((string projectName, string[] expectedReferences) in expected)
        {
            string projectPath = Path.Combine(sourceRoot, projectName, $"{projectName}.csproj");
            string[] actualReferences = XDocument.Load(projectPath)
                .Descendants("ProjectReference")
                .Select(element => Path.GetFileNameWithoutExtension((string?)element.Attribute("Include")))
                .Where(name => name is not null)
                .Order(StringComparer.Ordinal)
                .ToArray()!;

            Assert.Equal(expectedReferences.Order(StringComparer.Ordinal), actualReferences);
        }
    }

    [Fact]
    public void DesktopDoesNotReferenceRawFnecoreTypes()
    {
        string desktopRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Desktop");
        string[] violations = Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string source = File.ReadAllText(path);
                return source.Contains("using fnecore", StringComparison.Ordinal) ||
                       source.Contains("fnecore.", StringComparison.Ordinal);
            })
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

        Assert.Empty(violations);
    }

    [Fact]
    public void ProductionProjectsKeepApprovedPackageBoundaries()
    {
        string sourceRoot = FindSourceRoot();
        var expected = new Dictionary<string, string[]>
        {
            ["DvmConsole.CodeplugValidator"] = [],
            ["DvmConsole.Application"] = [],
            ["DvmConsole.Audio.Abstractions"] = [],
            ["DvmConsole.Audio.Core"] = ["Concentus.Oggfile", "NLayer"],
            ["DvmConsole.Audio.Desktop"] = [],
            ["DvmConsole.Audio.Linux"] = [],
            ["DvmConsole.Audio.MacOS"] = [],
            ["DvmConsole.Audio.Windows"] = ["NAudio.Wasapi"],
            ["DvmConsole.Configuration.Yaml"] = [],
            ["DvmConsole.Core"] = ["YamlDotNet"],
            ["DvmConsole.Desktop"] =
            [
                "Avalonia",
                "Avalonia.Desktop",
                "Avalonia.Native",
                "Avalonia.Skia",
                "Avalonia.Themes.Fluent",
                "Avalonia.Win32",
                "Avalonia.X11",
                "AvaloniaUI.DiagnosticsSupport",
                "Markdown.Avalonia.Tight",
                "Tmds.DBus.Protocol"
            ],
            ["DvmConsole.Fne"] = ["SharpZipLib"],
            ["DvmConsole.FneClient"] = [],
            ["DvmConsole.Media"] = [],
            ["DvmConsole.Operations"] = [],
            ["DvmConsole.Presentation"] = ["Avalonia", "Avalonia.Themes.Fluent"],
            ["DvmConsole.Ptt.Abstractions"] = [],
            ["DvmConsole.Ptt.Desktop"] = ["System.IO.Ports", "Tmds.DBus.Protocol"],
            ["DvmConsole.Storage"] = [],
            ["DvmConsole.Vocoder.Abstractions"] = [],
            ["DvmConsole.Vocoder.Native"] = []
        };

        foreach ((string projectName, string[] expectedPackages) in expected)
        {
            string projectPath = Path.Combine(sourceRoot, projectName, $"{projectName}.csproj");
            string[] actualPackages = XDocument.Load(projectPath)
                .Descendants("PackageReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(name => name is not null)
                .Order(StringComparer.Ordinal)
                .ToArray()!;

            Assert.Equal(expectedPackages.Order(StringComparer.Ordinal), actualPackages);
            if (!projectName.Equals("DvmConsole.Desktop", StringComparison.Ordinal) &&
                !projectName.Equals("DvmConsole.Presentation", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(
                    actualPackages,
                    package => package.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void ApplicationSourceIsFreeOfUiAndPlatformDependencies()
    {
        string applicationRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Application");
        string[] forbidden =
        [
            "Avalonia",
            "ViewModel",
            "OperatingSystem.Is",
            "System.IO.Ports",
            "StorageProvider",
            "P/Invoke",
            "DllImport"
        ];

        string[] violations = Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => forbidden.Any(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ApplicationRuntimeUsesInjectedClockAndMonotonicTimeSources()
    {
        string applicationRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Application");
        string[] violations = Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("HostServices.cs", StringComparison.Ordinal))
            .Where(path =>
            {
                string source = File.ReadAllText(path);
                return source.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal) ||
                       source.Contains("DateTimeOffset.Now", StringComparison.Ordinal) ||
                       source.Contains("Stopwatch.", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

        Assert.Empty(violations);
    }

    [Fact]
    public void DesktopSessionRuntimeUsesTheSchedulerBoundary()
    {
        string runtimePath = Path.Combine(
            FindSourceRoot(),
            "DvmConsole.Desktop",
            "ConsoleSessionRuntime.cs");
        string source = File.ReadAllText(runtimePath);

        Assert.Contains("IApplicationScheduler", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia.Threading", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationOwnsTheConsoleSessionFacade()
    {
        string sourceRoot = FindSourceRoot();
        string adapterPath = Path.Combine(
            sourceRoot,
            "DvmConsole.Desktop",
            "DesktopConsoleSessionRuntimeAdapter.cs");
        string legacyFacadePath = Path.Combine(
            sourceRoot,
            "DvmConsole.Desktop",
            "DesktopConsoleApplicationSession.cs");
        string adapterSource = File.ReadAllText(adapterPath);
        string hostSource = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Desktop",
            "MainWindowSessionHost.cs"));

        Assert.False(File.Exists(legacyFacadePath));
        Assert.Contains(": IConsoleSessionRuntimeAdapter", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IConsoleApplicationSession", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new ConsoleApplicationSession", adapterSource, StringComparison.Ordinal);
        Assert.Contains(
            "new ConsoleApplicationSession(new DesktopConsoleSessionRuntimeAdapter(",
            hostSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationStudioUsesTheSharedRuntimeContract()
    {
        string source = File.ReadAllText(Path.Combine(
            FindSourceRoot(),
            "DvmConsole.Presentation",
            "ConfigurationStudioViewModel.cs"));

        Assert.Contains("IConfigurationStudioRuntimeContext", source, StringComparison.Ordinal);
        Assert.Contains("IConfigurationStudioCompanionSource", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileSystemPathIdentity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UserSettingsStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigurationFileChange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateSavePlan", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigurationLoader.ResolvePath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SourcePath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChannelViewModel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WebStreamSelectionIdentity", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationSourceIsFreeOfDesktopNativeAndPathDependencies()
    {
        string presentationRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Presentation");
        string[] forbidden =
        [
            "Avalonia.Controls.Window",
            "StorageProvider",
            "IClassicDesktopStyleApplicationLifetime",
            "OperatingSystem.Is",
            "System.IO.Ports",
            "DllImport",
            "Process.Start",
            "DvmConsole.Desktop",
            "System.IO.Path",
            "System.IO.File",
            "System.IO.Directory",
            "FileStream",
            "FileInfo",
            "DirectoryInfo"
        ];
        string[] violations = Directory.EnumerateFiles(presentationRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("AssemblyInfo.cs", StringComparison.Ordinal))
            .Where(path => forbidden.Any(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void OperatorToolsMountsPortablePagesFromPresentation()
    {
        string shellSource = File.ReadAllText(Path.Combine(
            FindSourceRoot(),
            "DvmConsole.Desktop",
            "OperatorToolsWindow.axaml"));
        string hostSource = File.ReadAllText(Path.Combine(
            FindSourceRoot(),
            "DvmConsole.Desktop",
            "OperatorToolsWindow.Pages.cs"));
        string[] sharedPages =
        [
            "new GeneralSettingsView",
            "new AudioSettingsView",
            "new WebStreamsSettingsView",
            "new RecorderSettingsView",
            "new CallHistoryView",
            "new PttSettingsView",
            "new ToneSettingsView",
            "new GroupSettingsView",
            "new ConnectionsSettingsView"
        ];

        Assert.All(sharedPages, page => Assert.Contains(page, hostSource, StringComparison.Ordinal));
        Assert.Contains("x:Name=\"ToolContent\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<TabControl", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding ToolbarClocks}\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding WebStreams}\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordingRootPathText", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AudioInputGainText", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SerialPttPortOptions", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalPttKeyOptions", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding DtmfPresets}\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding ToneSequenceSteps}\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding AlertTones}\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding PatchGroups}\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding Systems}\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding KeyStatusItems}\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<DataTemplate", shellSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationStudioWindowMountsThePortablePresentationShell()
    {
        string desktopSource = File.ReadAllText(Path.Combine(
            FindSourceRoot(),
            "DvmConsole.Desktop",
            "ConfigurationStudioWindow.axaml"));
        string presentationSource = File.ReadAllText(Path.Combine(
            FindSourceRoot(),
            "DvmConsole.Presentation",
            "ConfigurationStudioView.axaml"));
        string[] sharedPages =
        [
            "presentation:ConfigurationStudioNavigationView",
            "presentation:ConfigurationStudioOverviewView",
            "presentation:ConfigurationStudioSystemsView",
            "presentation:ConfigurationStudioZonesView",
            "presentation:ConfigurationStudioStreamsView",
            "presentation:ConfigurationStudioGroupsView",
            "presentation:ConfigurationStudioKeysView",
            "presentation:ConfigurationStudioFilesView"
        ];

        Assert.Contains("presentation:ConfigurationStudioView", desktopSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<DataTemplate", desktopSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=", desktopSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigurationStudioZonesView", desktopSource, StringComparison.Ordinal);
        Assert.All(sharedPages, page => Assert.Contains(page, presentationSource, StringComparison.Ordinal));
        Assert.DoesNotContain("$parent[Window]", presentationSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", presentationSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationLibraryWindowMountsThePortablePresentationView()
    {
        string sourceRoot = FindSourceRoot();
        string desktopSource = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Desktop",
            "ConfigurationLibraryWindow.axaml"));
        string mainWindowSource = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Desktop",
            "MainWindow.axaml"));
        string mainWindowCode = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Desktop",
            "MainWindow.axaml.cs"));

        Assert.Contains("presentation:ConfigurationLibraryView", desktopSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<DataTemplate", desktopSource, StringComparison.Ordinal);
        Assert.Contains("Configuration Library…", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains(
            "await PublishManagedReplacementAsync(imported.Reference, replacement, materialization)",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await configurationLibrary.ActivateAsync(imported.Reference)",
            mainWindowCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopRuntimeConsumesRadioAudioVocoderAndPttFactoryContracts()
    {
        string desktopRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Desktop");
        string mainViewModel = File.ReadAllText(Path.Combine(desktopRoot, "MainWindowViewModel.cs"));
        string pttController = File.ReadAllText(Path.Combine(desktopRoot, "PttSessionController.cs"));
        string systemViewModel = File.ReadAllText(Path.Combine(desktopRoot, "SystemViewModel.cs"));
        string radioFactory = File.ReadAllText(Path.Combine(desktopRoot, "FneRadioSessionFactory.cs"));

        Assert.Contains("IAudioBackendFactory audioBackendFactory", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("IVocoderFactory vocoderFactory", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("audioBackendFactory.Create(", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("audioBackendFactory as IAudioDeviceChangeSourceFactory", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("audioBackendFactory as DesktopAudioBackendFactory", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("vocoderFactory.Create(", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("AudioBackendFactory.CreateDefault(", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("new SoftwareVocoderBackend", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Func<string, int, IPttInputSourceFactory>", pttController, StringComparison.Ordinal);
        Assert.Contains("IFneRadioSessionFactory? radioSessionFactory", systemViewModel, StringComparison.Ordinal);
        Assert.Contains("radioSession = factory.Create();", systemViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", systemViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("new FneRadioSessionAdapter", systemViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("FneTrafficReceived +=", systemViewModel, StringComparison.Ordinal);
        Assert.Contains(": IFneRadioSessionFactory, IRadioSessionFactory", radioFactory, StringComparison.Ordinal);

        string sessionSetup = File.ReadAllText(Path.Combine(
            desktopRoot,
            "MainWindowViewModel.SessionSetup.cs"));
        Assert.Contains("radioIngress.TrafficReceived +=", sessionSetup, StringComparison.Ordinal);
        Assert.Contains("radioIngress.AuthorityChanged +=", sessionSetup, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopDiagnosticsFeedTheApplicationLogStream()
    {
        string adapterSource = File.ReadAllText(Path.Combine(
            FindSourceRoot(),
            "DvmConsole.Desktop",
            "DesktopConsoleSessionRuntimeAdapter.cs"));

        Assert.Contains("owner.DebugLogPublished += HandleDebugLogPublished", adapterSource, StringComparison.Ordinal);
        Assert.Contains("LogPublished?.Invoke", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("add { }", adapterSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaUsesNeutralRadioFramesWithoutNetworkOrNativeVocoderDependencies()
    {
        string mediaRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Media");
        string[] forbidden =
        [
            "DvmConsole.FneClient",
            "FneTrafficFrame",
            "FneTrafficProtocol",
            "DvmConsole.Vocoder.Native"
        ];
        string[] violations = Directory.EnumerateFiles(mediaRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => forbidden.Any(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

        Assert.Empty(violations);
    }

    [Fact]
    public void MediaRecordingPrimitivesDoNotOwnFilesystemPaths()
    {
        string mediaRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Media");
        string[] forbidden =
        [
            "System.IO.Path",
            "Path.",
            "FileStream",
            "File.",
            "Directory.",
            "Environment.ProcessId"
        ];
        string[] violations = Directory.EnumerateFiles(mediaRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => forbidden.Any(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

        Assert.Empty(violations);

        string writerSource = File.ReadAllText(Path.Combine(mediaRoot, "PcmWavFileWriter.cs"))
            .ReplaceLineEndings("\n");
        string trimmerSource = File.ReadAllText(Path.Combine(mediaRoot, "PcmWavSilenceTrimmer.cs"))
            .ReplaceLineEndings("\n");
        Assert.Contains("PcmWavFileWriter(\n        Stream stream", writerSource, StringComparison.Ordinal);
        Assert.Contains("RepairInterruptedStream(Stream stream", writerSource, StringComparison.Ordinal);
        Assert.Contains("Stream source", trimmerSource, StringComparison.Ordinal);
        Assert.Contains("Stream destination", trimmerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableManagedAudioCodecsDoNotOwnFilesystemPaths()
    {
        string audioCoreRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Audio.Core");
        string[] forbidden =
        [
            "System.IO.Path",
            "Path.",
            "FileStream",
            "File.Open",
            "File.Exists",
            "File.Move",
            "File.Delete",
            "File.ReadAll",
            "File.WriteAll",
            "Directory.",
            "Environment.ProcessId"
        ];
        string[] violations = Directory.EnumerateFiles(audioCoreRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => forbidden.Any(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

        Assert.Empty(violations);

        string loaderSource = File.ReadAllText(Path.Combine(audioCoreRoot, "PcmAudioFileLoader.cs"))
            .ReplaceLineEndings("\n");
        string encoderSource = File.ReadAllText(Path.Combine(audioCoreRoot, "OpusRecordingEncoder.cs"))
            .ReplaceLineEndings("\n");
        string tagsSource = File.ReadAllText(Path.Combine(audioCoreRoot, "OggOpusTags.cs"))
            .ReplaceLineEndings("\n");
        Assert.Contains("LoadAsync(\n        Stream source", loaderSource, StringComparison.Ordinal);
        Assert.DoesNotContain("string path", loaderSource, StringComparison.Ordinal);
        Assert.Contains("EncodeWaveStreamAsync", encoderSource, StringComparison.Ordinal);
        Assert.Contains("Read(Stream stream)", tagsSource, StringComparison.Ordinal);
        Assert.Contains("Set(Stream input, Stream output", tagsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SerialPortPackageAndSymbolsAreConfinedToDesktopPtt()
    {
        string sourceRoot = FindSourceRoot();
        string[] violations = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("System.IO.Ports", StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}DvmConsole.Ptt.Desktop{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.EndsWith(nameof(ArchitectureBoundaryTests) + ".cs", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void RuntimeTransmitTonePatchAndRecordingServicesUseIdsAndImmutableDescriptors()
    {
        string sourceRoot = FindSourceRoot();
        (string Project, string File)[] serviceFiles =
        [
            ("DvmConsole.Application", "ChannelTransmitCoordinator.cs"),
            ("DvmConsole.Application", "ChannelReceiveAudioCoordinator.cs"),
            ("DvmConsole.Application", "ChannelReceiveWorkQueue.cs"),
            ("DvmConsole.Application", "ChannelAudioMeterPipeline.cs"),
            ("DvmConsole.Application", "ReceiveAudioRouteRegistry.cs"),
            ("DvmConsole.Application", "ReceiveEpisodePlaybackPool.cs"),
            ("DvmConsole.Application", "ReceiveSessionFactory.cs"),
            ("DvmConsole.Application", "PatchSourceDecodeCoordinator.cs"),
            ("DvmConsole.Application", "ToneTransmitCoordinator.cs"),
            ("DvmConsole.Application", "PatchForwardingCoordinator.cs"),
            ("DvmConsole.Application", "CallRecordingService.cs"),
            ("DvmConsole.Application", "RecordingPlaybackCoordinator.cs"),
            ("DvmConsole.Application", "RadioConnectionCoordinator.cs"),
            ("DvmConsole.Application", "ConsoleCallHistory.cs"),
            ("DvmConsole.Application", "GeneratedAudioMonitor.cs"),
            ("DvmConsole.Application", "LocalTonePlayer.cs"),
            ("DvmConsole.Application", "ApplicationAudioBackendProvider.cs"),
            ("DvmConsole.Application", "AudioRuntimeSettingsTransaction.cs"),
            ("DvmConsole.Application", "AdaptiveReceiveJitterBufferController.cs"),
            ("DvmConsole.Application", "ReceiveJitterBufferConfigurationPolicy.cs"),
            ("DvmConsole.Application", "ReceiveJitterBufferEffectivenessTracker.cs"),
            ("DvmConsole.Application", "ReceiveJitterEventReporter.cs"),
            ("DvmConsole.Application", "ConfigurationDraftIdentityRegistry.cs"),
            ("DvmConsole.Application", "ConfigurationIdentityMigrationPlanner.cs"),
            ("DvmConsole.Application", "ConfigurationStudioDraftHistory.cs"),
            ("DvmConsole.Application", "FixedBucketLatencyTracker.cs"),
            ("DvmConsole.Application", "PcmLevelWindowAccumulator.cs"),
            ("DvmConsole.Application", "LatestBooleanStateReconciler.cs"),
            ("DvmConsole.Application", "P25KeyRequestCoordinator.cs"),
            ("DvmConsole.Desktop", "CallRecordingManager.cs"),
            ("DvmConsole.Desktop", "RecordingPlaybackCoordinator.cs"),
            ("DvmConsole.Desktop", "RecordingFinalizationResult.cs"),
            ("DvmConsole.Desktop", "IFneTrafficEndpoint.cs")
        ];
        string[] violations = serviceFiles
            .Where(service =>
            {
                string source = File.ReadAllText(Path.Combine(
                    sourceRoot,
                    service.Project,
                    service.File));
                return source.Contains("ChannelViewModel", StringComparison.Ordinal) ||
                       source.Contains("SystemViewModel", StringComparison.Ordinal);
            })
            .Select(service => $"{service.Project}/{service.File}")
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void RecordingAndPatchIngressUseProtocolNeutralMediaFrames()
    {
        string sourceRoot = FindSourceRoot();
        (string Project, string File)[] serviceFiles =
        [
            ("DvmConsole.Desktop", "CallRecordingManager.cs"),
            ("DvmConsole.Application", "ChannelReceiveAudioCoordinator.cs"),
            ("DvmConsole.Application", "ChannelReceiveWorkQueue.cs"),
            ("DvmConsole.Application", "PatchSourceDecodeCoordinator.cs"),
            ("DvmConsole.Application", "PatchForwardingCoordinator.cs"),
            ("DvmConsole.Application", "CallRecordingService.cs")
        ];
        string[] violations = serviceFiles
            .Where(service => File.ReadAllText(Path.Combine(
                    sourceRoot,
                    service.Project,
                    service.File))
                .Contains("FneTrafficFrame", StringComparison.Ordinal))
            .Select(service => $"{service.Project}/{service.File}")
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void RemovedFfmpegAndHighQualityBluetoothSymbolsDoNotRemainInProduction()
    {
        string repositoryRoot = Directory.GetParent(FindSourceRoot())!.FullName;
        string[] productionRoots =
        [
            Path.Combine(repositoryRoot, "src"),
            Path.Combine(repositoryRoot, "native")
        ];
        string[] forbidden =
        [
            "DVM_FFMPEG",
            "FfmpegPcmStreamReader",
            "HighQualityBluetoothAudioEnabled",
            "high_quality_bluetooth"
        ];
        string[] violations = productionRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains(".Tests", StringComparison.Ordinal))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".m", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .Where(path => forbidden.Any(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void PortableProjectsEnableTrimAndAotAnalyzers()
    {
        string sourceRoot = FindSourceRoot();
        string[] projectNames =
        [
            "DvmConsole.Application",
            "DvmConsole.Audio.Abstractions",
            "DvmConsole.Audio.Core",
            "DvmConsole.Configuration.Yaml",
            "DvmConsole.Core",
            "DvmConsole.Media",
            "DvmConsole.Operations",
            "DvmConsole.Ptt.Abstractions",
            "DvmConsole.Presentation",
            "DvmConsole.Storage",
            "DvmConsole.Vocoder.Abstractions"
        ];

        foreach (string projectName in projectNames)
        {
            XDocument document = XDocument.Load(Path.Combine(sourceRoot, projectName, $"{projectName}.csproj"));
            Assert.Equal("true", document.Descendants("IsTrimmable").Single().Value);
            Assert.Equal("true", document.Descendants("EnableTrimAnalyzer").Single().Value);
            Assert.Equal("true", document.Descendants("EnableAotAnalyzer").Single().Value);
        }
    }

    [Fact]
    public void NativeVocoderDefinesLinuxRuntimeTargetsAndSharedLibraryName()
    {
        string projectPath = Path.Combine(
            FindSourceRoot(),
            "DvmConsole.Vocoder.Native",
            "DvmConsole.Vocoder.Native.csproj");
        XDocument project = XDocument.Load(projectPath);
        IReadOnlyDictionary<string, string> targets = project
            .Descendants("NativeVocoderTarget")
            .Where(element => element.Attribute("Condition") is not null)
            .ToDictionary(
                element => element.Attribute("Condition")!.Value,
                element => element.Value,
                StringComparer.Ordinal);

        Assert.Equal(
            "x86_64-unknown-linux-gnu",
            targets["'$(RuntimeIdentifier)' == 'linux-x64'"]);
        Assert.Equal(
            "aarch64-unknown-linux-gnu",
            targets["'$(RuntimeIdentifier)' == 'linux-arm64'"]);

        string projectText = File.ReadAllText(projectPath);
        Assert.Contains(
            "'$(RuntimeIdentifier)' == 'linux-x64' or '$(RuntimeIdentifier)' == 'linux-arm64'",
            projectText,
            StringComparison.Ordinal);
        Assert.Contains(
            ">libdvmconsole_vocoder.so</NativeVocoderLibraryName>",
            projectText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsArm64HasANativeSingleFileReleaseTarget()
    {
        string repositoryRoot = Directory.GetParent(FindSourceRoot())!.FullName;
        string projectPath = Path.Combine(
            FindSourceRoot(),
            "DvmConsole.Vocoder.Native",
            "DvmConsole.Vocoder.Native.csproj");
        string project = File.ReadAllText(projectPath);
        string publish = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "publish-desktop.sh"));
        string publishPowerShell = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "publish-desktop.ps1"));
        string verifyPowerShell = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "verify-publish.ps1"));
        string workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "build.yml"));
        string taggedPublisher = workflow.Split("  publish-release:", StringSplitOptions.None)[1];

        Assert.Contains("'$(RuntimeIdentifier)' == 'win-arm64'", project, StringComparison.Ordinal);
        Assert.Contains("aarch64-pc-windows-msvc", project, StringComparison.Ordinal);
        Assert.Contains("NativeVocoderCargoExtension", project, StringComparison.Ordinal);
        Assert.Contains("win-arm64)", publish, StringComparison.Ordinal);
        Assert.Contains("win-arm64", publishPowerShell, StringComparison.Ordinal);
        Assert.Contains("0xAA64", verifyPowerShell, StringComparison.Ordinal);
        Assert.Contains("os: windows-11-arm", workflow, StringComparison.Ordinal);
        Assert.Contains("rid: win-arm64", workflow, StringComparison.Ordinal);
        Assert.Contains("startsWith(matrix.rid, 'win-')", workflow, StringComparison.Ordinal);
        Assert.Contains("win-arm64", taggedPublisher, StringComparison.Ordinal);
    }

    [Fact]
    public void UnixWindowsX64PublishPrefersTheReproducibleMsvcToolchain()
    {
        string repositoryRoot = Directory.GetParent(FindSourceRoot())!.FullName;
        string publish = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "publish-desktop.sh"));
        int cargoXwin = publish.IndexOf(
            "elif cargo xwin --version >/dev/null 2>&1; then",
            StringComparison.Ordinal);
        int mingw = publish.IndexOf(
            "elif command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then",
            StringComparison.Ordinal);

        Assert.True(cargoXwin >= 0, "The Unix publisher must detect cargo-xwin.");
        Assert.True(mingw > cargoXwin, "MSVC must be preferred before the MinGW fallback.");
    }

    [Fact]
    public void PlatformPublishKeepsRidSpecificRestoreStateOutOfTheDefaultObjDirectory()
    {
        string repositoryRoot = Directory.GetParent(FindSourceRoot())!.FullName;
        string publish = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "publish-desktop.sh"));
        string publishPowerShell = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "publish-desktop.ps1"));
        string buildProperties = File.ReadAllText(Path.Combine(FindSourceRoot(), "Directory.Build.props"));

        Assert.Equal(2, publish.Split("DvmConsolePackageRuntime", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, publishPowerShell.Split("DvmConsolePackageRuntime", StringSplitOptions.None).Length - 1);
        Assert.Contains("DvmConsolePackageIntermediateRoot", buildProperties, StringComparison.Ordinal);
        Assert.Contains("MSBuildProjectExtensionsPath", buildProperties, StringComparison.Ordinal);
        Assert.Contains("IntermediateOutputPath", buildProperties, StringComparison.Ordinal);
        Assert.Contains("$(MSBuildProjectDirectory)", buildProperties, StringComparison.Ordinal);
        Assert.Contains("DvmConsoleIsolatedBuildRoot=\"$BUILD_ROOT\"", publish, StringComparison.Ordinal);
        Assert.Contains("DvmConsoleIsolatedBuildRoot=$BuildRoot", publishPowerShell, StringComparison.Ordinal);
        Assert.Contains("mktemp -d \"/tmp/dvmconsole-publish-build.", publish, StringComparison.Ordinal);
        Assert.Contains("$(DvmConsoleIsolatedBuildRoot)/obj/$(MSBuildProjectName)/", buildProperties, StringComparison.Ordinal);
        Assert.Contains("$(DvmConsoleIsolatedBuildRoot)/bin/$(MSBuildProjectName)/", buildProperties, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformPublishRemovesTransientBuildIdentityFromUnsignedPackages()
    {
        string sourceRoot = FindSourceRoot();
        string buildProperties = File.ReadAllText(Path.Combine(sourceRoot, "Directory.Build.props"));
        string vocoderProject = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Vocoder.Native",
            "DvmConsole.Vocoder.Native.csproj"));

        Assert.Contains("<Deterministic>true</Deterministic>", buildProperties, StringComparison.Ordinal);
        Assert.Contains("<ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>", buildProperties, StringComparison.Ordinal);
        Assert.Contains("<DeterministicSourcePaths>true</DeterministicSourcePaths>", buildProperties, StringComparison.Ordinal);
        Assert.Contains("<TrimmerRemoveSymbols>true</TrimmerRemoveSymbols>", buildProperties, StringComparison.Ordinal);
        Assert.Contains("<PathMap>", buildProperties, StringComparison.Ordinal);
        Assert.Contains("--new-mvid false</_ExtraTrimmerArgs>", buildProperties, StringComparison.Ordinal);
        Assert.DoesNotContain("-Wl,-no_uuid", vocoderProject, StringComparison.Ordinal);
        Assert.DoesNotContain("-Wl,-random_uuid", vocoderProject, StringComparison.Ordinal);
        Assert.Contains("default content-based LC_UUID", vocoderProject, StringComparison.Ordinal);
        Assert.Equal(2, vocoderProject.Split("@rpath/libdvmconsole_vocoder.dylib", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, vocoderProject.Split("-C link-arg=/Brepro", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, vocoderProject.Split("-C link-arg=/DEBUG:NONE", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void MacOsPublishRequiresLoadableMachOUuids()
    {
        string repositoryRoot = Directory.GetParent(FindSourceRoot())!.FullName;
        string verifier = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "verify-publish.sh"));

        Assert.Contains("verify_macos_uuid", verifier, StringComparison.Ordinal);
        Assert.Contains("LC_UUID", verifier, StringComparison.Ordinal);
        Assert.Contains("cannot be loaded reliably by macOS", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxDesktopTargetUsesX11AndOnlyTheLinuxAudioAdapter()
    {
        string sourceRoot = FindSourceRoot();
        string desktopProject = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Desktop",
            "DvmConsole.Desktop.csproj"));
        string audioProject = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Audio.Desktop",
            "DvmConsole.Audio.Desktop.csproj"));
        string program = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Desktop",
            "Program.cs"));
        string permissions = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Desktop",
            "DesktopPrivacyPermissionService.cs"));

        Assert.Contains("DVMCONSOLE_LINUX", desktopProject, StringComparison.Ordinal);
        Assert.Contains("Avalonia.X11", desktopProject, StringComparison.Ordinal);
        Assert.Contains(".UseX11()", program, StringComparison.Ordinal);
        Assert.Contains("DvmConsole.Audio.Linux.csproj", audioProject, StringComparison.Ordinal);
        Assert.Contains(
            "'$(DvmConsoleTargetPlatform)' == '' or '$(DvmConsoleTargetPlatform)' == 'linux'",
            audioProject,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Condition=\"'$(DvmConsoleTargetPlatform)' != 'windows'\"",
            audioProject,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Condition=\"'$(DvmConsoleTargetPlatform)' != 'macos'\"",
            audioProject,
            StringComparison.Ordinal);
        Assert.DoesNotContain("#if !DVMCONSOLE_WINDOWS", permissions, StringComparison.Ordinal);
        Assert.Contains("#if DVMCONSOLE_MACOS", permissions, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxAudioNativeShimUsesPipeWireAndTheSharedLockFreePcmRing()
    {
        string repositoryRoot = Directory.GetParent(FindSourceRoot())!.FullName;
        string nativeRoot = Path.Combine(repositoryRoot, "native", "dvmaudio-pipewire");
        string cmake = File.ReadAllText(Path.Combine(nativeRoot, "CMakeLists.txt"));
        string source = File.ReadAllText(Path.Combine(nativeRoot, "dvmaudio_pipewire.c"));
        string[] managedExports = File.ReadAllLines(Path.Combine(nativeRoot, "managed-exports.txt"));
        string captureSignal = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "native",
            "dvmaudio",
            "dvm_capture_signal.c"));
        string linuxAudioRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Audio.Linux");
        string nativeLoader = File.ReadAllText(Path.Combine(linuxAudioRoot, "NativePipeWireApi.cs"));
        string monitorLoader = File.ReadAllText(Path.Combine(linuxAudioRoot, "LinuxPipeWireDeviceChangeSource.cs"));
        string contract = File.ReadAllText(Path.Combine(linuxAudioRoot, "PipeWireNativeContract.cs"));
        string endpoints = File.ReadAllText(Path.Combine(linuxAudioRoot, "LinuxPipeWireEndpoints.cs"));

        Assert.Contains("libpipewire-0.3", cmake, StringComparison.Ordinal);
        Assert.Contains("../dvmaudio/dvm_pcm_ring.c", cmake, StringComparison.Ordinal);
        Assert.Contains("pw_stream_new_simple", source, StringComparison.Ordinal);
        Assert.Contains("PW_STREAM_FLAG_AUTOCONNECT", source, StringComparison.Ordinal);
        Assert.Contains("PW_STREAM_FLAG_RT_PROCESS", source, StringComparison.Ordinal);
        Assert.Contains("pw_context_connect", source, StringComparison.Ordinal);
        Assert.Contains("pw_core_get_registry", source, StringComparison.Ordinal);
        Assert.Contains("PW_KEY_OBJECT_SERIAL", source, StringComparison.Ordinal);
        Assert.Contains("PW_KEY_TARGET_OBJECT", source, StringComparison.Ordinal);
        Assert.Contains("bluez", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dvm_capture_signal_notify", source, StringComparison.Ordinal);
        Assert.Contains("poll(&descriptor", captureSignal, StringComparison.Ordinal);
        Assert.Equal(21, managedExports.Length);
        Assert.Equal(managedExports.Length, managedExports.Distinct(StringComparer.Ordinal).Count());
        Assert.All(managedExports, symbol => Assert.Contains(symbol, source, StringComparison.Ordinal));
        Assert.Contains("PipeWireNativeContract.ValidateExports", nativeLoader, StringComparison.Ordinal);
        Assert.Contains("PipeWireNativeContract.ValidateExports", monitorLoader, StringComparison.Ordinal);
        Assert.Contains("GetManifestResourceStream", contract, StringComparison.Ordinal);
        Assert.Contains("WaitForCapture", endpoints, StringComparison.Ordinal);
        Assert.Contains("WakeCapture", endpoints, StringComparison.Ordinal);
        Assert.Contains("Timeout.Infinite", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("PeriodicTimer", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void MacAudioCaptureWaitsForNativeReadinessInsteadOfPolling()
    {
        string repositoryRoot = Directory.GetParent(FindSourceRoot())!.FullName;
        string nativeSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "native",
            "dvmaudio",
            "dvmaudio.c"));
        string macAudioRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Audio.MacOS");
        string nativeLoader = File.ReadAllText(Path.Combine(macAudioRoot, "NativeCoreAudioApi.cs"));
        string endpoints = File.ReadAllText(Path.Combine(macAudioRoot, "MacCoreAudioEndpoints.cs"));

        Assert.Contains("dvm_audio_stream_wait_for_capture", nativeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("dvm_audio_voice_processing_", nativeSource, StringComparison.Ordinal);
        Assert.Contains("dvm_capture_signal_notify", nativeSource, StringComparison.Ordinal);
        Assert.Contains("dvm_audio_stream_wake_capture", nativeLoader, StringComparison.Ordinal);
        Assert.DoesNotContain("dvm_audio_voice_processing_", nativeLoader, StringComparison.Ordinal);
        Assert.Contains("WaitForCapture", endpoints, StringComparison.Ordinal);
        Assert.Contains("WakeCapture", endpoints, StringComparison.Ordinal);
        Assert.Contains("Timeout.Infinite", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("PeriodicTimer", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxPublishPipelineBuildsSingleFileAppImageReleaseTargets()
    {
        string repositoryRoot = Directory.GetParent(FindSourceRoot())!.FullName;
        string publish = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "publish-desktop.sh"));
        string verify = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "verify-publish.sh"));
        string package = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "package-desktop-linux-appimage.sh"));
        string smoke = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "smoke-desktop-linux-appimage.sh"));
        string containerSmoke = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "smoke-desktop-linux-container.sh"));
        string portableBuild = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "build-linux-portable.sh"));
        string portableDockerfile = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "packaging",
            "linux",
            "Dockerfile"));
        string workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "build.yml"));
        string pipeWireProbe = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "probe-linux-pipewire.sh"));
        string taggedPublisher = workflow.Split("  publish-release:", StringSplitOptions.None)[1];

        Assert.Contains("linux-x64)", publish, StringComparison.Ordinal);
        Assert.Contains("linux-arm64)", publish, StringComparison.Ordinal);
        Assert.Contains("native/dvmaudio-pipewire", publish, StringComparison.Ordinal);
        Assert.Contains("libdvmaudio-pipewire.so", publish, StringComparison.Ordinal);
        Assert.Contains("libdvmconsole_vocoder.so", verify, StringComparison.Ordinal);
        Assert.Contains("libpipewire-0.3.so", verify, StringComparison.Ordinal);
        Assert.Equal(1, verify.Split("linux-x64|linux-arm64)", StringSplitOptions.None).Length - 1);
        Assert.Contains("MAXIMUM_LINUX_GLIBC_VERSION=\"2.34\"", verify, StringComparison.Ordinal);
        Assert.Contains("readelf --version-info", verify, StringComparison.Ordinal);
        Assert.Contains("DVMConsole.AppDir", package, StringComparison.Ordinal);
        Assert.Contains("--runtime-file", package, StringComparison.Ordinal);
        Assert.Contains("--comp zstd", package, StringComparison.Ordinal);
        Assert.Contains("--mksquashfs-opt 22", package, StringComparison.Ordinal);
        Assert.Contains("xvfb-run", smoke, StringComparison.Ordinal);
        Assert.Contains("APPIMAGE_EXTRACT_AND_RUN=1", smoke, StringComparison.Ordinal);
        Assert.Contains("--smoke-windows", smoke, StringComparison.Ordinal);
        Assert.Contains("packaging/linux", portableBuild, StringComparison.Ordinal);
        Assert.Contains("scripts/publish-desktop.sh", portableBuild, StringComparison.Ordinal);
        Assert.Contains("scripts/package-desktop-linux-appimage.sh", portableBuild, StringComparison.Ordinal);
        Assert.Contains("scripts/smoke-desktop-linux-appimage.sh", portableBuild, StringComparison.Ordinal);
        Assert.Contains("CARGO_TARGET_DIR=/tmp/dvmconsole-native-target", portableBuild, StringComparison.Ordinal);
        Assert.Contains("DVM_AUDIO_BUILD_DIR=/tmp/dvmconsole-pipewire-build", portableBuild, StringComparison.Ordinal);
        Assert.Contains("DVM_RELEASE_VERSION=$DVM_RELEASE_VERSION", portableBuild, StringComparison.Ordinal);
        Assert.Contains("DVM_PACKAGE_VERSION=$DVM_PACKAGE_VERSION", portableBuild, StringComparison.Ordinal);
        Assert.Contains("rm -rf \"$ARTIFACT_ROOT/$RID\"", portableBuild, StringComparison.Ordinal);
        Assert.Contains("FROM debian:12-slim@sha256:", portableDockerfile, StringComparison.Ordinal);
        Assert.Contains("ARG RUST_TOOLCHAIN=1.85.0", portableDockerfile, StringComparison.Ordinal);
        Assert.Contains("ARG TARGETARCH", portableDockerfile, StringComparison.Ordinal);
        Assert.Contains("appimagetool/releases/download/1.9.1", portableDockerfile, StringComparison.Ordinal);
        Assert.Contains("type2-runtime/releases/download/20251108", portableDockerfile, StringComparison.Ordinal);
        Assert.Contains("ARG DOTNET_SDK_VERSION=10.0.400", portableDockerfile, StringComparison.Ordinal);
        Assert.Contains("dotnet-sdk-${DOTNET_SDK_VERSION}-linux-${dotnet_arch}.tar.gz", portableDockerfile, StringComparison.Ordinal);
        Assert.Contains("sha512sum --check", portableDockerfile, StringComparison.Ordinal);
        Assert.Contains("snapshot.debian.org/archive/debian/${DEBIAN_SNAPSHOT}", portableDockerfile, StringComparison.Ordinal);
        Assert.Contains("libpipewire-0.3-dev", portableDockerfile, StringComparison.Ordinal);
        Assert.Contains("libicu", containerSmoke, StringComparison.Ordinal);
        Assert.Contains("pipewire-libs", containerSmoke, StringComparison.Ordinal);

        Assert.Contains("os: ubuntu-24.04", workflow, StringComparison.Ordinal);
        Assert.Contains("os: ubuntu-24.04-arm", workflow, StringComparison.Ordinal);
        Assert.Contains("rid: linux-x64", workflow, StringComparison.Ordinal);
        Assert.Contains("rid: linux-arm64", workflow, StringComparison.Ordinal);
        Assert.Contains("DVM_PACKAGE_NAME", workflow, StringComparison.Ordinal);
        Assert.Contains(".AppImage", workflow, StringComparison.Ordinal);
        Assert.Contains("libpipewire-0.3-dev", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify native Linux PipeWire audio shim", workflow, StringComparison.Ordinal);
        Assert.Contains("managed-exports.txt", workflow, StringComparison.Ordinal);
        Assert.Contains("native/dvmaudio/managed-exports.txt", workflow, StringComparison.Ordinal);
        Assert.Contains("native/vocoder/managed-exports.txt", workflow, StringComparison.Ordinal);
        Assert.Contains("Probe live PipeWire ABI and device enumeration", workflow, StringComparison.Ordinal);
        Assert.Contains("--linux-devices", pipeWireProbe, StringComparison.Ordinal);
        Assert.Contains("Publish and package portable Linux release", workflow, StringComparison.Ordinal);
        Assert.Contains("Smoke packaged Linux payload", workflow, StringComparison.Ordinal);
        Assert.Contains("Smoke the same Linux package across distro baselines", workflow, StringComparison.Ordinal);
        Assert.Contains("debian:12-slim@sha256:", workflow, StringComparison.Ordinal);
        Assert.Contains("debian:13-slim@sha256:", workflow, StringComparison.Ordinal);
        Assert.Contains("ubuntu:22.04@sha256:", workflow, StringComparison.Ordinal);
        Assert.Contains("ubuntu:24.04@sha256:", workflow, StringComparison.Ordinal);
        Assert.Contains("ubuntu:26.04@sha256:", workflow, StringComparison.Ordinal);
        Assert.Contains("fedora:43@sha256:", workflow, StringComparison.Ordinal);
        Assert.Contains("linux-x64", taggedPublisher, StringComparison.Ordinal);
        Assert.Contains("linux-arm64", taggedPublisher, StringComparison.Ordinal);
        Assert.Contains("*.AppImage", taggedPublisher, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxGlobalKeyboardPttUsesX11ObservationOrTheWaylandShortcutPortal()
    {
        string sourceRoot = FindSourceRoot();
        string capture = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Ptt.Desktop",
            "LinuxX11GlobalKeyboardCapture.cs"));
        string portal = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Ptt.Desktop",
            "LinuxPortalGlobalKeyboardCapture.cs"));
        string source = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Ptt.Desktop",
            "GlobalKeyboardPttSource.cs"));

        Assert.Contains("OperatingSystem.IsLinux()", source, StringComparison.Ordinal);
        Assert.Contains("XRecordCreateContext", capture, StringComparison.Ordinal);
        Assert.Contains("XRecordEnableContext", capture, StringComparison.Ordinal);
        Assert.Contains("XSync(controlDisplay", capture, StringComparison.Ordinal);
        Assert.Contains("XkbKeycodeToKeysym(controlDisplay", capture, StringComparison.Ordinal);
        Assert.Contains("XkbSetDetectableAutoRepeat", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("XGrabKey", capture, StringComparison.Ordinal);
        Assert.Contains("LinuxDesktopSession.IsWayland", source, StringComparison.Ordinal);
        Assert.Contains("org.freedesktop.portal.GlobalShortcuts", portal, StringComparison.Ordinal);
        Assert.Contains("org.freedesktop.host.portal.Registry", portal, StringComparison.Ordinal);
        Assert.Contains("org.dvmproject.dvmconsole", portal, StringComparison.Ordinal);
        Assert.Contains("RegisterHostApplicationAsync", portal, StringComparison.Ordinal);
        Assert.Contains("CreateSession", portal, StringComparison.Ordinal);
        Assert.Contains("BindShortcuts", portal, StringComparison.Ordinal);
        Assert.Contains("Activated", portal, StringComparison.Ordinal);
        Assert.Contains("Deactivated", portal, StringComparison.Ordinal);
        Assert.Contains("Func<Connection, string, MessageBuffer>", portal, StringComparison.Ordinal);
        Assert.Contains("return writer.CreateMessage();", portal, StringComparison.Ordinal);
        Assert.DoesNotContain("Action<MessageWriter", portal, StringComparison.Ordinal);
        Assert.DoesNotContain("Arg0Path =", portal, StringComparison.Ordinal);
        Assert.DoesNotContain("Arg0 = expectedSessionPath", portal, StringComparison.Ordinal);
        Assert.Contains("notification.SessionPath == expectedSessionPath", portal, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxLifecycleObservesLogindSuspendAndResume()
    {
        string sourceRoot = FindSourceRoot();
        string monitor = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Desktop",
            "LinuxLogindSleepMonitor.cs"));
        string lifecycle = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Desktop",
            "DesktopApplicationLifecycle.cs"));

        Assert.Contains("org.freedesktop.login1.Manager", monitor, StringComparison.Ordinal);
        Assert.Contains("PrepareForSleep", monitor, StringComparison.Ordinal);
        Assert.Contains("ReadBool", monitor, StringComparison.Ordinal);
        Assert.Contains("NotifySuspending", lifecycle, StringComparison.Ordinal);
        Assert.Contains("NotifyResumed", lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void FneRuntimeUsesTheRootOwnedAotSafeUtilityMirror()
    {
        string sourceRoot = FindSourceRoot();
        XDocument project = XDocument.Load(Path.Combine(
            sourceRoot,
            "DvmConsole.Fne",
            "DvmConsole.Fne.csproj"));
        string projectText = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Fne",
            "DvmConsole.Fne.csproj"));
        string fneUtilities = File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Fne",
            "FneUtils.cs"));

        Assert.Equal("true", project.Descendants("IsTrimmable").Single().Value);
        Assert.Equal("true", project.Descendants("EnableTrimAnalyzer").Single().Value);
        Assert.Equal("true", project.Descendants("EnableAotAnalyzer").Single().Value);
        Assert.Contains("../../fnecore/FneUtils.cs", projectText, StringComparison.Ordinal);
        Assert.Contains("<Compile Include=\"FneUtils.cs\" />", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("new DynamicMethod(", fneUtilities, StringComparison.Ordinal);
    }

    [Fact]
    public void YamlConfigurationUsesTheTypedAotSafeCodecWithoutAllowlists()
    {
        string coreRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Core");
        string project = File.ReadAllText(Path.Combine(coreRoot, "DvmConsole.Core.csproj"));
        string token = "UnconditionalSuppressMessage(\"AOT\", \"IL3050\"";
        string[] sourcePaths = Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories)
            .ToArray();
        string[] sources = sourcePaths
            .Select(File.ReadAllText)
            .ToArray();
        int suppressions = sources.Sum(source =>
            source.Split(token, StringSplitOptions.None).Length - 1);
        string codec = File.ReadAllText(Path.Combine(
            coreRoot,
            "Configuration",
            "DvmYamlCodec.cs"));

        Assert.Equal(0, suppressions);
        Assert.DoesNotContain(sources, source =>
            source.Contains("DeserializerBuilder", StringComparison.Ordinal) ||
            source.Contains("SerializerBuilder", StringComparison.Ordinal));
        Assert.Contains("YamlStream", codec, StringComparison.Ordinal);
        Assert.Contains("ReadConfiguration", codec, StringComparison.Ordinal);
        Assert.Contains("CreateConfigurationTree", codec, StringComparison.Ordinal);
        Assert.DoesNotContain("IL3050", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationDelayLoopsUseTheInjectedDelayBoundary()
    {
        string applicationRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Application");
        string[] directDelayOwners = Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("Task.Delay(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

        Assert.Equal(["ChannelTransmitCoordinator.cs", "HostServices.cs"], directDelayOwners);
        Assert.Contains(
            "Task.Delay(interval, timeProvider, cancellationToken)",
            File.ReadAllText(Path.Combine(applicationRoot, "ChannelTransmitCoordinator.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            "IApplicationDelay Delay",
            File.ReadAllText(Path.Combine(applicationRoot, "HostServices.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompiledDesktopAssemblyDoesNotReferenceTheRawFneAssembly()
    {
        string[] references = typeof(MainWindowViewModel).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("DvmConsole.Fne", references);
        Assert.DoesNotContain(references, reference =>
            reference.Contains("fnecore", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindSourceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src");
            if (File.Exists(Path.Combine(directory.FullName, "dvmconsole.sln")) && Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository source directory.");
    }
}
