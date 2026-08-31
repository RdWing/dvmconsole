using System.Xml.Linq;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ArchitectureBoundaryTests
{
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
                ["DvmConsole.Audio.Abstractions", "DvmConsole.Audio.Core", "DvmConsole.Audio.MacOS", "DvmConsole.Audio.Windows"],
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
            ["DvmConsole.Storage"] = ["DvmConsole.Application"],
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
                "AvaloniaUI.DiagnosticsSupport",
                "Markdown.Avalonia"
            ],
            ["DvmConsole.Fne"] = ["SharpZipLib"],
            ["DvmConsole.FneClient"] = [],
            ["DvmConsole.Media"] = [],
            ["DvmConsole.Operations"] = [],
            ["DvmConsole.Presentation"] = ["Avalonia", "Avalonia.Themes.Fluent"],
            ["DvmConsole.Ptt.Abstractions"] = [],
            ["DvmConsole.Ptt.Desktop"] = ["System.IO.Ports"],
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
            "await PublishManagedReplacementAsync(imported.Reference, replacement)",
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
        Assert.Contains("vocoderFactory.Create(", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("AudioBackendFactory.CreateDefault(", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("new SoftwareVocoderBackend", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("Func<string, int, IPttInputSourceFactory>", pttController, StringComparison.Ordinal);
        Assert.Contains("IRadioSessionFactory? radioSessionFactory", systemViewModel, StringComparison.Ordinal);
        Assert.Contains(".CreateAsync(descriptor)", systemViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("new FneRadioSessionAdapter", systemViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("FneTrafficReceived +=", systemViewModel, StringComparison.Ordinal);
        Assert.Contains(": IRadioSessionFactory", radioFactory, StringComparison.Ordinal);

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
    public void FneRuntimeCodeGenerationHasAnExplicitPhaseTwoAnalyzerAllowlist()
    {
        string sourceRoot = FindSourceRoot();
        XDocument project = XDocument.Load(Path.Combine(
            sourceRoot,
            "DvmConsole.Fne",
            "DvmConsole.Fne.csproj"));
        string fneUtilities = File.ReadAllText(Path.Combine(
            sourceRoot,
            "..",
            "fnecore",
            "FneUtils.cs"));

        Assert.Equal("false", project.Descendants("IsTrimmable").Single().Value);
        Assert.Equal("false", project.Descendants("EnableTrimAnalyzer").Single().Value);
        Assert.Equal("false", project.Descendants("EnableAotAnalyzer").Single().Value);
        Assert.Contains("Temporary Phase 2 trim/AOT allowlist", File.ReadAllText(Path.Combine(
            sourceRoot,
            "DvmConsole.Fne",
            "DvmConsole.Fne.csproj")), StringComparison.Ordinal);
        Assert.Contains("DynamicMethod", fneUtilities, StringComparison.Ordinal);
    }

    [Fact]
    public void YamlAotCompatibilityUsesOnlyTheFiniteCallSiteAllowlist()
    {
        string coreRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Core");
        string project = File.ReadAllText(Path.Combine(coreRoot, "DvmConsole.Core.csproj"));
        string token = "UnconditionalSuppressMessage(\"AOT\", \"IL3050\"";
        string[] sources = Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        int suppressions = sources.Sum(source =>
            source.Split(token, StringSplitOptions.None).Length - 1);

        Assert.Equal(8, suppressions);
        Assert.All(
            sources.Where(source => source.Contains(token, StringComparison.Ordinal)),
            source => Assert.Contains("Temporary Phase 2 YAML allowlist", source, StringComparison.Ordinal));
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
