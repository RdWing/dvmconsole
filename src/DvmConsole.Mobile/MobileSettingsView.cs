// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Mobile;

/// <summary>Settings navigation retains the configuration page while returning to the console.</summary>
public sealed class MobileSettingsView : UserControl
{
    public event EventHandler? ConsoleRequested;
    private CancellationTokenSource? pendingToneLoad;
    private bool needsListeningRetry;

    private void CancelPendingToneLoad() => pendingToneLoad?.Cancel();

    private readonly TextBlock startupStatus = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public void ShowStartupFailure(string message)
    {
        startupStatus.Text = $"The saved configuration could not be opened: {message}";
        startupStatus.IsVisible = true;
    }

    public void ShowSessionFollowUpFailure(SessionReplacementFollowUpPhase phase, string detail)
    {
        needsListeningRetry = phase != SessionReplacementFollowUpPhase.RetiredSessionCleanup;
        startupStatus.Text = phase == SessionReplacementFollowUpPhase.RetiredSessionCleanup
            ? $"The new configuration is open, but cleanup of the previous session was incomplete: {detail}"
            : $"The configuration is open, but listening could not resume: {detail} Use Resume listening to retry.";
        startupStatus.IsVisible = true;
        Navigate(overview);
    }

    public void OpenConfigurationLibrary() => Navigate(configurationPage);

    public void ClearStartupFailure() => startupStatus.IsVisible = false;

    private readonly Grid configurationPage;
    private readonly Grid overview = new();
    private readonly Grid navigationShell = new() { ColumnDefinitions = new("0,*") };
    private readonly ContentControl detail = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private ConsoleHostFormFactor hostFormFactor;
    private bool showingOverview = true;
    private bool studioOpen;
    private Button? selectedNavigation;
    private readonly TextBlock emptyDetail = new()
    {
        Text = "Choose a Settings category",
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap
    };

    private void Navigate(Control page)
    {
        CancelPendingToneLoad();
        showingOverview = ReferenceEquals(page, overview);
        detail.Content = showingOverview ? emptyDetail : page;
        UpdateNavigationLayout();
    }

    private void UpdateNavigationLayout()
    {
        bool split = hostFormFactor == ConsoleHostFormFactor.Tablet && Bounds.Width >= 840 && !studioOpen && !showingOverview;
        navigationShell.ColumnDefinitions[0].Width = new GridLength(split ? 300 : 0);
        Grid.SetColumn(overview, split ? 0 : 1);
        overview.IsVisible = split || showingOverview;
        detail.IsVisible = split || !showingOverview;
    }

    private Button Category(string title, Control body, Control? fixedControl = null)
    {
        var back = new Button { Content = "‹ Settings", MinHeight = 44 };
        var page = MobileSettingsPageLayout.Create(MobileSettingsPageLayout.Heading(title, back), body);
        if (fixedControl is not null)
        {
            page.RowDefinitions = new("Auto,Auto,*");
            Grid.SetRow(page.Children[1], 2);
            Grid.SetRow(fixedControl, 1);
            page.Children.Add(fixedControl);
        }
        back.Click += (_, _) => Navigate(overview);
        var button = new Button
        {
            Content = title + "  ›",
            MinHeight = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(button, "Open " + title);
        button.Click += (_, _) => Navigate(page);
        return button;
    }

    public MobileSettingsView(Control configuration, ConsoleHostFormFactor formFactor, MobileConsoleView console)
        : this(configuration, formFactor, () => console) { }

    public MobileSettingsView(Control configuration, ConsoleHostFormFactor formFactor, Func<MobileConsoleView> console, Func<MobileSession>? session = null,
        Control? audioRoutePicker = null, Func<IConsoleHelpCatalog>? createHelpCatalog = null, Control? audioInputSettings = null)
    {
        hostFormFactor = formFactor;
        var body = new StackPanel { Spacing = 16 };
        var consoleBack = new Button { Content = "‹ Console", MinHeight = 44 };
        AutomationProperties.SetName(consoleBack, "Back to Console");
        consoleBack.Click += (_, _) =>
        {
            Navigate(overview);
            ConsoleRequested?.Invoke(this, EventArgs.Empty);
        };
        DetachedFromVisualTree += (_, _) => CancelPendingToneLoad();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == ContentProperty) CancelPendingToneLoad();
        };
        var heading = MobileSettingsPageLayout.Heading("Settings", consoleBack);
        body.Children.Add(startupStatus);
        var navigation = Section("Configuration");
        var connectionSection = Section("Connections");
        var audioSection = Section("Audio");
        var transmitSection = Section("Push to Talk");
        var recordingSection = Section("History");
        var diagnosticsSection = Section("Diagnostics");
        var supportSection = Section("History and support");
        body.Children.Add(navigation);

        var library = new Button
        {
            Content = "Configuration Library  ›",
            MinHeight = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(library, "Open Configuration Library");
        navigation.Children.Add(library);

        if (session is not null)
        {
            connectionSection.Children.Add(new MobileConnectionsView(session));
            connectionSection.Children.Add(new MobileListeningSettingsView(session));
            transmitSection.Children.Add(new MobileManualTransmitSettingsView(session));
            var processing = new Button
            {
                Content = "Microphone Processing  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(processing, "Open Microphone Processing");
            processing.Click += (_, _) =>
            {
                Control parentPage = (Control)detail.Content!;
                var page = new MobileMicrophoneProcessingView(session);
                page.SettingsRequested += (_, _) => Navigate(parentPage);
                Navigate(page);
            };
            audioSection.Children.Add(processing);
            var receiveProcessing = new Button
            {
                Content = "Receive Processing  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(receiveProcessing, "Open Receive Processing");
            receiveProcessing.Click += (_, _) =>
            {
                Control parentPage = (Control)detail.Content!;
                var page = new MobileReceiveProcessingView(session);
                page.SettingsRequested += (_, _) => Navigate(parentPage);
                Navigate(page);
            };
            audioSection.Children.Add(receiveProcessing);
            var connectionStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
            connectionSection.Children.Add(connectionStatus);
            var resume = new Button { Content = "Resume listening", MinHeight = 48, IsVisible = false };
            IConsoleApplicationSession? observedSession = null;
            void RefreshRecovery() => resume.IsVisible = needsListeningRetry || session().Execution?.Snapshot.State == ConsoleExecutionState.RequiresResume;
            void RecoveryChanged(object? sender, ConsoleSnapshotChangedEventArgs args) => Dispatcher.UIThread.Post(RefreshRecovery);
            resume.AttachedToVisualTree += (_, _) =>
            {
                observedSession = session().Application;
                observedSession.SnapshotChanged += RecoveryChanged;
                RefreshRecovery();
            };
            resume.DetachedFromVisualTree += (_, _) =>
            {
                if (observedSession is not null) observedSession.SnapshotChanged -= RecoveryChanged;
                observedSession = null;
            };
            resume.Click += async (_, _) =>
            {
                connectionStatus.IsVisible = true;
                if (session().ResumeListening is not { } recover)
                { connectionStatus.Text = "Open a configuration first."; return; }
                resume.IsEnabled = false;
                try { await recover(CancellationToken.None); needsListeningRetry = false; connectionStatus.Text = "Listening resumed."; }
                catch (Exception exception) { connectionStatus.Text = exception.Message; }
                finally { resume.IsEnabled = true; RefreshRecovery(); }
            };
            connectionSection.Children.Add(resume);
        }
        Control? fixedAudioOutput = null;
        if (audioRoutePicker is not null)
        {
            // Native UIKit controls stay outside Avalonia's scrolling clip.
            var output = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
            output.Children.Add(new TextBlock
            {
                Text = "Choose system output",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(audioRoutePicker, 1);
            output.Children.Add(audioRoutePicker);
            fixedAudioOutput = output;
        }
        if (audioInputSettings is not null)
            audioSection.Children.Add(new Border { Padding = new Thickness(14, 12), Child = audioInputSettings });
        var togglePtt = new ToggleSwitch
        {
            MinHeight = 48,
            Content = new TextBlock { Text = "Tap to toggle PTT", TextWrapping = TextWrapping.Wrap }
        };
        AutomationProperties.SetName(togglePtt, "Tap to toggle PTT");
        var pttStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        togglePtt.AttachedToVisualTree += (_, _) => togglePtt.IsChecked = console().TogglePttMode;
        togglePtt.IsCheckedChanged += async (_, _) =>
        {
            MobileConsoleView current = console();
            if (current.TogglePttMode == (togglePtt.IsChecked == true)) return;
            togglePtt.IsEnabled = false;
            pttStatus.IsVisible = false;
            try { await current.SetTogglePttModeAsync(togglePtt.IsChecked == true); }
            catch (Exception exception) { pttStatus.Text = exception.Message; pttStatus.IsVisible = true; }
            finally { togglePtt.IsChecked = console().TogglePttMode; togglePtt.IsEnabled = true; }
        };
        transmitSection.Children.Add(togglePtt);
        transmitSection.Children.Add(new TextBlock
        {
            Text = "Off: hold to talk. On: tap to start and stop. Saved for this configuration on this device.",
            TextWrapping = TextWrapping.Wrap
        });
        transmitSection.Children.Add(pttStatus);

        var operation = Section("Operations");
        body.Children.Add(operation);
        if (session is not null) operation.Children.Add(Category("Connections", connectionSection));
        operation.Children.Add(Category("Audio", audioSection, fixedAudioOutput));
        operation.Children.Add(Category("Push to Talk", transmitSection));

        if (session is not null || createHelpCatalog is not null) body.Children.Add(supportSection);
        overview = MobileSettingsPageLayout.Create(heading, body);
        var back = new Button { Content = "‹ Settings", MinHeight = 48, Margin = new Thickness(16, 8, 16, 0) };
        AutomationProperties.SetName(back, "Back to Settings");
        configurationPage = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        configurationPage.Children.Add(back);
        Grid.SetRow(configuration, 1);
        configurationPage.Children.Add(configuration);
        void UpdateNestedNavigation()
        {
            studioOpen = configuration is MobileConfigurationStudioView ||
                (configuration as MobileConfigurationView)?.Content is MobileConfigurationStudioView;
            back.IsVisible = !studioOpen;
            UpdateNavigationLayout();
        }
        configuration.PropertyChanged += (_, change) =>
        {
            if (change.Property == ContentControl.ContentProperty) UpdateNestedNavigation();
        };
        UpdateNestedNavigation();
        library.Click += (_, _) => OpenConfigurationLibrary();
        if (configuration is MobileConfigurationView { SupportsStudio: true } configurationView)
        {
            var studio = new Button
            {
                Content = "Edit active configuration  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(studio, "Open Configuration Studio");
            navigation.Children.Insert(2, studio);
            studio.Click += async (_, _) =>
            {
                Navigate(configurationPage);
                await configurationView.Initialization;
                await configurationView.OpenActiveStudioAsync(() => Navigate(overview));
            };
        }
        back.Click += (_, _) => Navigate(overview);
        if (session is not null)
        {
            var openBuffering = new Button
            {
                Content = "Receive Buffering  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            openBuffering.Click += (_, _) =>
            {
                Control parentPage = (Control)detail.Content!;
                var buffering = new MobileReceiveBufferingView(session);
                buffering.SettingsRequested += (_, _) => Navigate(parentPage);
                Navigate(buffering);
            };
            connectionSection.Children.Add(openBuffering);
            var openRecordings = new Button
            {
                Content = "Retention  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            openRecordings.Click += (_, _) =>
            {
                var recordings = new MobileRecordingSettingsView(session);
                recordings.SettingsRequested += (_, _) => Navigate(overview);
                Navigate(recordings);
            };
            supportSection.Children.Add(openRecordings);
            var openStreams = new Button
            {
                Content = "Web Streams  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            openStreams.Click += (_, _) =>
            {
                var streams = new MobileWebStreamsView(session());
                streams.SettingsRequested += (_, _) => Navigate(overview);
                Navigate(streams);
            };
            operation.Children.Add(openStreams);
            var openGroups = new Button
            {
                Content = "Groups and Patches  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            openGroups.Click += (_, _) =>
            {
                var groups = new MobileGroupView(session());
                groups.SettingsRequested += (_, _) => Navigate(overview);
                Navigate(groups);
            };
            operation.Children.Insert(Math.Min(4, operation.Children.Count), openGroups);
            var openSubscribers = new Button
            {
                Content = "Subscriber Commands  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            openSubscribers.Click += (_, _) =>
            {
                var subscribers = new MobileSubscriberCommandsView(session());
                subscribers.SettingsRequested += (_, _) => Navigate(overview);
                Navigate(subscribers);
            };
            operation.Children.Add(openSubscribers);
            var logs = new MobileLogsView(session);
            var openTones = new Button
            {
                Content = "Tones and Paging  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            openTones.Click += async (_, _) =>
            {
                openTones.IsEnabled = false;
                using var cancellation = new CancellationTokenSource();
                pendingToneLoad = cancellation;
                var tones = new MobileToneView();
                bool published = false;
                try
                {
                    var current = session();
                    await tones.OpenAsync(current, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(current.Application, session().Application)) return;
                    tones.SettingsRequested += (_, _) => Navigate(overview);
                    pendingToneLoad = null;
                    Navigate(tones);
                    published = true;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                finally
                {
                    if (ReferenceEquals(pendingToneLoad, cancellation)) pendingToneLoad = null;
                    if (!published) await tones.DisposeAsync();
                    openTones.IsEnabled = true;
                }
            };
            operation.Children.Insert(Math.Min(4, operation.Children.Count), openTones);
            var openLogs = new Button
            {
                Content = "Logs  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(openLogs, "Open Logs");
            openLogs.Click += (_, _) => Navigate(logs);
            logs.SettingsRequested += (_, _) => Navigate(overview);
            diagnosticsSection.Children.Add(openLogs);
            var openHealth = new Button
            {
                Content = "Engineering Health  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(openHealth, "Open Engineering Health");
            openHealth.Click += (_, _) =>
            {
                var health = new MobileEngineeringHealthView(session);
                health.SettingsRequested += (_, _) => Navigate(overview);
                Navigate(health);
            };
            diagnosticsSection.Children.Add(openHealth);
            var history = new MobileHistoryView(session);
            var openHistory = new Button
            {
                Content = "History  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(openHistory, "Open History");
            supportSection.Children.Insert(1, openHistory);
            supportSection.Children.Add(Category("Diagnostics", diagnosticsSection));
            openHistory.Click += (_, _) => Navigate(history);
            history.SettingsRequested += (_, _) => Navigate(overview);
        }
        if (createHelpCatalog is not null)
        {
            var openHelp = new Button
            {
                Content = "Help  ›",
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(openHelp, "Open Help");
            openHelp.Click += (_, _) =>
            {
                var help = new MobileHelpView(createHelpCatalog, formFactor);
                help.SettingsRequested += (_, _) => Navigate(overview);
                Navigate(help);
            };
            supportSection.Children.Add(openHelp);
        }
        supportSection.Children.Add(Category("About", new MobileAboutView()));
        foreach (var row in body.GetLogicalDescendants().OfType<Button>())
        {
            row.Classes.Add("settings-navigation");
            row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            row.ContentTemplate = new FuncDataTemplate<string>((label, _) =>
            {
                var content = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
                var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                labels.Children.Add(new TextBlock { Text = label?.TrimEnd(' ', '›'), TextWrapping = TextWrapping.Wrap });
                string? subtitle = label?.StartsWith("Configuration Library", StringComparison.Ordinal) == true
                    ? "Create, import and manage" : label?.StartsWith("Edit active", StringComparison.Ordinal) == true
                    ? "Open in Studio" : null;
                if (subtitle is not null) labels.Children.Add(new TextBlock
                { Text = subtitle, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 }.WithScaledFontSize(13));
                content.Children.Add(labels);
                var disclosure = new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(disclosure, 1);
                content.Children.Add(disclosure);
                return content;
            });
            row.Click += (_, _) =>
            {
                selectedNavigation?.Classes.Remove("selected-settings-category");
                selectedNavigation = row;
                row.Classes.Add("selected-settings-category");
            };
        }
        MobileSettingsSurface.GroupSection(navigation);
        MobileSettingsSurface.GroupSection(operation);
        MobileSettingsSurface.GroupSection(supportSection);
        MobileSettingsSurface.GroupSection(audioSection);
        MobileSettingsSurface.GroupSection(recordingSection);
        MobileSettingsSurface.GroupSection(diagnosticsSection);
        navigationShell.Children.Add(overview);
        Grid.SetColumn(detail, 1);
        navigationShell.Children.Add(detail);
        Content = navigationShell;
        SizeChanged += (_, _) => UpdateNavigationLayout();
        Navigate(overview);
        this.Bind(BackgroundProperty, this.GetResourceObservable("ShellBackgroundBrush"));
    }
    private static StackPanel Section(string title)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(MobileSettingsSurface.Caption(title));
        return panel;
    }

}
