using System.Globalization;
using System.Xml.Linq;
using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class OperatorInterfaceGateTests
{
    [Fact]
    public void FileMenuSeparatesExternalImportFromManagedRecentConfigurations()
    {
        string shell = ReadDesktopSource("MainWindow.axaml");

        Assert.Contains("Header=\"Import Codeplug…\"", shell, StringComparison.Ordinal);
        Assert.Contains("Header=\"Open Recent\" x:Name=\"recentManagedConfigurationsMenu\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Open Codeplug…\"", shell, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(880, false, false, false, true)]
    [InlineData(919, false, false, false, true)]
    [InlineData(920, true, false, false, true)]
    [InlineData(999, true, false, false, true)]
    [InlineData(1000, true, false, true, true)]
    [InlineData(1119, true, false, true, true)]
    [InlineData(1120, true, true, true, false)]
    [InlineData(1260, true, true, true, false)]
    [InlineData(1920, true, true, true, false)]
    public void ResponsiveToolbarShedsConvenienceContentBeforeOperationalControls(
        double width,
        bool expectedClocks,
        bool expectedAlertShortcuts,
        bool expectedTonesLauncher,
        bool expectedOverflow)
    {
        ResponsiveToolbarVisibility visibility = MainWindowResponsiveToolbarPolicy.Evaluate(width);

        Assert.Equal(expectedClocks, visibility.ShowClocks);
        Assert.Equal(expectedAlertShortcuts, visibility.ShowAlertToneShortcuts);
        Assert.Equal(expectedTonesLauncher, visibility.ShowTonesLauncher);
        Assert.Equal(expectedOverflow, visibility.ShowOverflow);

        string shell = ReadDesktopSource("MainWindow.axaml");
        Assert.Contains("x:Name=\"toolbarClocks\"", shell, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"toolbarAlertToneShortcuts\"", shell, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"toolbarTonesLauncher\"", shell, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"toolbarOverflowMenu\"", shell, StringComparison.Ordinal);

        XDocument document = XDocument.Parse(shell);
        XElement clocks = document.Descendants()
            .Single(element => Attribute(element, "Name") == "toolbarClocks");
        XElement microphoneControl = document.Descendants()
            .Single(element => Attribute(element, "Classes")?.Split(' ').Contains("mic-warm") == true);
        XElement alertControls = document.Descendants()
            .Single(element => Attribute(element, "Name") == "toolbarAlertToneShortcuts");
        XElement actionGroup = microphoneControl.Ancestors()
            .First(element => Attribute(element, "Grid.Column") == "3");

        Assert.Equal("Grid", clocks.Parent?.Name.LocalName);
        Assert.Equal("Auto,*,Auto,Auto", Attribute(clocks.Parent!, "ColumnDefinitions"));
        Assert.Equal("2", Attribute(clocks, "Grid.Column"));
        Assert.Equal("Left", Attribute(clocks, "HorizontalAlignment"));
        Assert.Equal("StackPanel", actionGroup.Name.LocalName);
        Assert.Contains(alertControls, actionGroup.Descendants());
    }

    [Theory]
    [InlineData(1260, 1.25, 1, true, false, true, true)]
    [InlineData(1399, 1.25, 1, true, false, true, true)]
    [InlineData(1400, 1.25, 1, true, true, true, false)]
    [InlineData(1260, 1.50, 1, false, false, false, true)]
    [InlineData(1120, 1.00, 2, true, false, true, true)]
    [InlineData(1200, 1.00, 2, true, true, true, false)]
    public void ResponsiveToolbarAccountsForScaleAndAdditionalClocks(
        double width,
        double uiScale,
        int enabledClockCount,
        bool expectedClocks,
        bool expectedAlertShortcuts,
        bool expectedTonesLauncher,
        bool expectedOverflow)
    {
        ResponsiveToolbarVisibility visibility = MainWindowResponsiveToolbarPolicy.Evaluate(
            width,
            uiScale,
            enabledClockCount);

        Assert.Equal(expectedClocks, visibility.ShowClocks);
        Assert.Equal(expectedAlertShortcuts, visibility.ShowAlertToneShortcuts);
        Assert.Equal(expectedTonesLauncher, visibility.ShowTonesLauncher);
        Assert.Equal(expectedOverflow, visibility.ShowOverflow);
    }

    [Fact]
    public void MainWindowHasOneClassicCardShellAndNoWorkspaceSwitcher()
    {
        string shell = ReadDesktopSource("MainWindow.axaml");
        string cards = ReadDesktopSource("ChannelCardsRenderer.axaml");
        string code = ReadDesktopSource("MainWindow.axaml.cs");
        string commands = ReadDesktopSource("OperatorCommandCatalog.cs");

        Assert.Contains("ItemsControl Classes=\"channel-canvas\"", cards, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"channelRendererHost\"", shell, StringComparison.Ordinal);
        XDocument document = XDocument.Parse(shell);
        XElement cardsMenuItem = document.Descendants()
            .Single(element => Attribute(element, "Name") == "cardsRendererMenuItem");
        XElement listMenuItem = document.Descendants()
            .Single(element => Attribute(element, "Name") == "listRendererMenuItem");
        Assert.Equal("Cards", Attribute(cardsMenuItem, "Header"));
        Assert.Equal("List", Attribute(listMenuItem, "Header"));
        Assert.Equal("Radio", Attribute(cardsMenuItem, "ToggleType"));
        Assert.Equal("Radio", Attribute(listMenuItem, "ToggleType"));
        Assert.Equal("Channel view", Attribute(cardsMenuItem.Parent!, "Header"));
        Assert.Equal("View", Attribute(cardsMenuItem.Parent!.Parent!, "Header"));
        Assert.DoesNotContain("cardsRendererButton", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("listRendererButton", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("NeoWorkspaceView", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Workspace\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeNeoWorkspace", code, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace.classic", commands, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace.neo", commands, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace.matrix", commands, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MacOsPackagedSmokeUsesAnIsolatedDemoProfile()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "smoke-desktop-macos.sh"));

        Assert.Contains(
            "--args --demo --smoke-windows",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EngineeringHealthIsAnOptionalTelemetryOnlyView()
    {
        XDocument shell = XDocument.Parse(ReadDesktopSource("MainWindow.axaml"));
        XElement menuItem = shell.Descendants()
            .Single(element => Attribute(element, "Name") == "engineeringHealthMenuItem");
        XElement paneHost = shell.Descendants()
            .Single(element => Attribute(element, "Name") == "engineeringHealthPane");

        Assert.Equal("Engineering Health", Attribute(menuItem, "Header"));
        Assert.Equal("CheckBox", Attribute(menuItem, "ToggleType"));
        Assert.Equal("view.engineering-health", Attribute(menuItem, "Tag"));
        Assert.Equal("HandleOperatorCommandClick", Attribute(menuItem, "Click"));
        Assert.Equal("False", Attribute(paneHost, "IsVisible"));

        XDocument pane = XDocument.Parse(ReadDesktopSource("EngineeringHealthPane.axaml"));
        Assert.DoesNotContain(
            pane.Descendants(),
            element => element.Name.LocalName is "Button" or "ToggleButton" or "ScrollViewer");
        Assert.Equal(
            4,
            pane.Descendants().Count(element => Attribute(element, "Classes") == "health-cell"));
        foreach (string binding in new[]
                 {
                     "ReceiveQueueHealthText",
                     "ReceiveLatencyHealthText",
                     "MicrophoneHealthText",
                     "TransmitBacklogHealthText",
                     "FinalizationHealthText",
                     "CatalogHealthText",
                     "RouteRecoveryHealthText",
                     "ConnectionHealthText"
                 })
        {
            Assert.Contains($"{{Binding {binding}}}", pane.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClassicChannelCardRetainsGeometryActionsAndBrushBindings()
    {
        XDocument cards = XDocument.Parse(ReadDesktopSource("ChannelCardsRenderer.axaml"));
        XElement card = cards.Descendants()
            .Single(element => Attribute(element, "Classes") == "channel-card");

        Assert.Equal("{Binding CardWidth}", Attribute(card, "Width"));
        Assert.Contains("ChannelCardHeight", Attribute(card, "Height"), StringComparison.Ordinal);
        Assert.Equal("{Binding CardBackgroundBrush}", Attribute(card, "Background"));
        Assert.Equal("{Binding CardBorderBrush}", Attribute(card, "BorderBrush"));
        Assert.Equal("5,5,5,4", Attribute(card, "Padding"));

        XElement sharedCard = card.Elements().Single(element => element.Name.LocalName == "ChannelCardContent");
        Assert.Equal("HandleTransmitSelectionClick", Attribute(sharedCard, "TransmitSelectionClick"));
        Assert.Equal("HandlePageSelectionClick", Attribute(sharedCard, "PageSelectionClick"));
        Assert.Equal("HandleAlertSelectionClick", Attribute(sharedCard, "AlertSelectionClick"));

        XDocument sharedCardDocument = XDocument.Parse(ReadPresentationSource("ChannelCardContent.axaml"));
        XElement layout = sharedCardDocument.Root!.Elements().Single(element => element.Name.LocalName == "Grid");
        Assert.Equal("Auto,Auto,Auto,*,24", Attribute(layout, "RowDefinitions"));
        AssertCardAction(layout, "encryption-select", "EncryptionSelectionBrush", "EncryptionSelectionBorderBrush");
        AssertCardAction(layout, "tx-multi", "TransmitSelectionBrush", "TransmitSelectionBorderBrush");
        AssertCardAction(layout, "page-select", "PageSelectionBrush", "PageSelectionBorderBrush");
        AssertCardAction(layout, "alert-select", "AlertSelectionBrush", "AlertSelectionBorderBrush");
        AssertCardAction(layout, "tar-select", "RecordingSelectionBrush", "RecordingSelectionBorderBrush");
        XElement pttInputGuard = Assert.Single(layout.Descendants(), element =>
            element.Name.LocalName == "Border" &&
            Attribute(element, "Classes") == "ptt-input-guard");
        Assert.Equal("Transparent", Attribute(pttInputGuard, "Background"));
        Assert.Single(layout.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            Attribute(element, "Classes")?.Split(' ').Contains("ptt") == true);
        Assert.DoesNotContain(layout.Descendants(), element => element.Name.LocalName == "ToggleButton");

        XDocument studio = XDocument.Parse(ReadPresentationSource("ConfigurationStudioZonesView.axaml"));
        XElement studioCard = studio.Descendants()
            .Single(element => Attribute(element, "Classes") == "channel-card");
        Assert.Single(studioCard.Elements(), element => element.Name.LocalName == "ChannelCardContent");
    }

    [Fact]
    public void ClassicChannelPttColorDoesNotChangeForTransientPointerStates()
    {
        XDocument shell = XDocument.Parse(ReadDesktopSource("MainWindow.axaml"));
        XElement[] pttPointerStyles = shell.Descendants()
            .Where(element => element.Name.LocalName == "Style")
            .Where(element => Attribute(element, "Selector") is string selector &&
                selector.StartsWith("Button.ptt:", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(pttPointerStyles);
        Assert.All(pttPointerStyles, style => Assert.All(
            style.Elements().Where(element => element.Name.LocalName == "Setter" &&
                Attribute(element, "Property") == "Background"),
            setter => Assert.Equal(
                "{DynamicResource PttBackgroundBrush}",
                Attribute(setter, "Value"))));
    }

    [Fact]
    public void ListControlColorsOnlyFollowOperationalStateClasses()
    {
        XDocument list = XDocument.Parse(ReadPresentationSource("ChannelListView.axaml"));
        XElement[] controls = list.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .ToArray();
        Assert.NotEmpty(controls);
        Assert.All(controls, control => Assert.Contains(
            "list-control",
            Attribute(control, "Classes")?.Split(' ') ?? []));
        Assert.Single(list.Descendants(), element =>
            element.Name.LocalName == "Style" &&
            Attribute(element, "Selector") == "Button.list-control" &&
            element.Descendants().Any(descendant => descendant.Name.LocalName == "ControlTemplate"));

        XDocument shell = XDocument.Parse(ReadDesktopSource("MainWindow.axaml"));
        Assert.Single(shell.Descendants(), element =>
            element.Name.LocalName == "Style" &&
            Attribute(element, "Selector") is string selector &&
            selector.Contains(":not(.list-control):pointerover", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationStudioSeparatesOperatorGroupChangesFromYamlSave()
    {
        XDocument studio = XDocument.Parse(ReadPresentationSource("ConfigurationStudioView.axaml"));
        XDocument sharedGroups = XDocument.Parse(ReadPresentationSource("ConfigurationStudioGroupsView.axaml"));
        XElement applyAndClose = sharedGroups.Descendants().Single(element =>
            Attribute(element, "Name") == "applyOperatorChangesAndCloseButton");
        XElement yamlSave = studio.Descendants().Single(element =>
            Attribute(element, "Click") == "HandleReviewAndSaveClick");

        Assert.Equal("Apply & close", Attribute(applyAndClose, "Content"));
        Assert.Equal("HandleApplyOperatorGroupsAndCloseClick", Attribute(applyAndClose, "Click"));
        Assert.Equal("{Binding ReviewSaveButtonText}", Attribute(yamlSave, "Content"));
        Assert.Equal("{Binding CanSaveDraft}", Attribute(yamlSave, "IsEnabled"));
    }

    [Fact]
    public void ChannelAudioMeterClipsAFullWidthThresholdScale()
    {
        XDocument sharedCard = XDocument.Parse(ReadPresentationSource("ChannelCardContent.axaml"));
        XElement meter = sharedCard.Descendants()
            .Single(element => Attribute(element, "Classes") == "channel-audio-meter");
        XElement fillClip = meter.Descendants()
            .Single(element => Attribute(element, "Classes") == "channel-audio-meter-fill-clip");
        XElement colorScale = fillClip.Elements()
            .Single(element => element.Name.LocalName == "Border");

        Assert.Equal("{Binding AudioFillWidth}", Attribute(fillClip, "Width"));
        Assert.Equal("True", Attribute(fillClip, "ClipToBounds"));
        Assert.Equal("{Binding AudioMeterWidth}", Attribute(colorScale, "Width"));
        Assert.DoesNotContain(fillClip.Descendants(), element => element.Name.LocalName == "ScaleTransform");

        string[] offsets = colorScale.Descendants()
            .Where(element => element.Name.LocalName == "GradientStop")
            .Select(element => Attribute(element, "Offset")!)
            .ToArray();
        Assert.Equal(["0", "0.76", "0.76", "0.88", "0.88", "1"], offsets);
        Assert.Equal(
            ChannelAudioMeter.YellowThresholdDisplayLevel / 100,
            double.Parse(offsets[1], CultureInfo.InvariantCulture));
        Assert.Equal(
            ChannelAudioMeter.RedThresholdDisplayLevel / 100,
            double.Parse(offsets[3], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SemanticTokensMeetTextAndNonTextContrastTargetsInBothThemes()
    {
        XDocument document = XDocument.Parse(ReadDesktopSource("App.axaml"));
        XElement[] themes = document.Descendants()
            .Where(element => element.Name.LocalName == "ResourceDictionary")
            .Where(element => Attribute(element, "Key") is "Light" or "Dark")
            .ToArray();

        Assert.Equal(2, themes.Length);
        foreach (XElement theme in themes)
        {
            string themeName = Attribute(theme, "Key")!;
            Dictionary<string, string> colors = theme.Elements()
                .Where(element => element.Name.LocalName == "SolidColorBrush")
                .ToDictionary(
                    element => Attribute(element, "Key")!,
                    element => Attribute(element, "Color")!,
                    StringComparer.Ordinal);

            AssertContrast(themeName, colors, "PrimaryTextBrush", "ShellBackgroundBrush", 4.5);
            AssertContrast(themeName, colors, "MutedTextBrush", "ShellBackgroundBrush", 4.5);
            AssertContrast(themeName, colors, "OperationalMeterFillBrush", "OperationalMeterTrackBrush", 3.0);
        }
    }

    private static void AssertCardAction(
        XElement card,
        string className,
        string backgroundBinding,
        string borderBinding)
    {
        XElement action = card.Descendants().Single(element =>
            element.Name.LocalName == "Button" &&
            Attribute(element, "Classes")?.Split(' ').Contains(className) == true);
        Assert.Equal($"{{Binding {backgroundBinding}}}", Attribute(action, "Background"));
        Assert.Equal($"{{Binding {borderBinding}}}", Attribute(action, "BorderBrush"));
    }

    private static void AssertContrast(
        string theme,
        IReadOnlyDictionary<string, string> colors,
        string foreground,
        string background,
        double minimum)
    {
        Assert.True(colors.ContainsKey(foreground), $"{theme} is missing {foreground}.");
        Assert.True(colors.ContainsKey(background), $"{theme} is missing {background}.");
        double ratio = ContrastRatio(colors[foreground], colors[background]);
        Assert.True(
            ratio >= minimum,
            $"{theme} {foreground} on {background} is {ratio:0.00}:1; expected at least {minimum:0.0}:1.");
    }

    private static double ContrastRatio(string first, string second)
    {
        double firstLuminance = RelativeLuminance(first);
        double secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(string color)
    {
        string value = color.TrimStart('#');
        if (value.Length == 8)
            value = value[2..];
        Assert.Equal(6, value.Length);

        double Channel(int offset)
        {
            double component = int.Parse(
                value.AsSpan(offset, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture) / 255d;
            return component <= 0.04045
                ? component / 12.92
                : Math.Pow((component + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }

    private static string? Attribute(XElement element, string localName)
        => element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName)?
            .Value;

    private static string ReadDesktopSource(string fileName)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "DvmConsole.Desktop", fileName));

    private static string ReadPresentationSource(string fileName)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "DvmConsole.Presentation", fileName));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "dvmconsole.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate dvmconsole.sln from the test output directory.");
    }
}
