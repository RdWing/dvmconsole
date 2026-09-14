// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DvmConsole.Application;
using DvmConsole.Presentation;
using DvmConsole.Threading;

namespace DvmConsole.Mobile;

public class MobileApp(ConsoleHostFormFactor formFactor) : Avalonia.Application
{
    private MobileConsoleView? console;
    private ConsoleSessionHost? sessions;
    private bool hostForeground = true;
    private bool consoleVisible = true;
    public ConsoleExecutionPolicy Execution { get; private set; } = new();

    public void SetHostForeground(bool foreground)
    {
        hostForeground = foreground;
        UpdateManualAdmission();
    }

    private void UpdateManualAdmission()
    {
        Execution.SetForeground(hostForeground);
        Execution.SetManualControlsAvailable(consoleVisible);
        if (!hostForeground || !consoleVisible)
            TaskObservation.Observe(console?.ReleaseManualTransmitAsync().AsTask() ?? Task.CompletedTask);
    }

    public MobileApp() : this(ConsoleHostFormFactor.Phone) { }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        SetPreferredTextScale(1);
    }

    protected void SetPreferredTextScale(double scale) => MobileTypography.Apply(Resources, scale);

    protected virtual Func<IConfigurationLibrary>? CreateConfigurationLibrary => null;

    protected virtual Func<IConfigurationExportArchive>? CreateExportArchive => null;

    protected virtual Func<IConfigurationLibrary, ConfigurationReference, CancellationToken, ValueTask<MobileStudioSession>>? CreateStudio => null;
    protected virtual Func<IConfigurationLibrary, ConfigurationDraft, CancellationToken, ValueTask<MobileStudioSession>>? CreateDraftStudio => null;

    protected virtual bool RestoreSavedSessionOnStartup => true;

    protected virtual IMobileLayoutPreferences? LayoutPreferences => null;

    protected virtual Func<Control>? CreateAudioRoutePicker => null;
    protected virtual Func<Control>? CreateAudioInputSettings => null;
    protected virtual Func<IConsoleHelpCatalog>? CreateHelpCatalog => null;

    protected virtual Func<IConfigurationLibrary, ConfigurationReference, CancellationToken, ValueTask<MobileSession>>? CreateSession => null;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime lifetime)
        {
            var initialSession = new ConsoleApplicationSession(
                new ConsoleTopologySnapshot(null, [], [], []), ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands());
            console = new MobileConsoleView(formFactor, LayoutPreferences, Execution,
                applicationSession: initialSession, ownsSession: false);
            var page = new ContentControl();
            MobileSettingsView? settings = null;
            MobileConfigurationView? configurationView = null;
            void Select(bool showConsole)
            {
                consoleVisible = showConsole;
                UpdateManualAdmission();
                page.Content = showConsole ? console : settings;
            }
            void Publish(IConsoleSessionAttachment attachment)
            {
                settings?.ClearStartupFailure();
                console = ((MobileSessionAttachment)attachment).View;
                Execution = console.Execution;
                console.SettingsRequested += (_, _) => Select(false);
                console.ConfigurationRequested += (_, _) =>
                {
                    settings?.OpenConfigurationLibrary();
                    Select(false);
                };
                Select(true);
            }
            sessions = new ConsoleSessionHost(new MobileSessionAttachment(console, new(console.ApplicationSession)),
                Publish, () => { }, () => page.Content = null);
            async Task OpenConsole(IConfigurationLibrary library, ConfigurationReference configuration, CancellationToken token)
            {
                await sessions.PrepareForReplacementAsync(token);
                MobileSession prepared = await CreateSession!(library, configuration, token);
                MobileSessionAttachment? replacement = null;
                bool handedOff = false;
                try
                {
                    ConsoleExecutionPolicy preparedExecution = prepared.Execution ?? new ConsoleExecutionPolicy();
                    preparedExecution.SetForeground(hostForeground);
                    preparedExecution.SetManualControlsAvailable(consoleVisible);
                    replacement = new(new MobileConsoleView(formFactor, LayoutPreferences, preparedExecution,
                        prepared.Application, ownsSession: false, toneSettings: prepared.ToneSettings, connectionStates: prepared.Connections as IConsoleConnectionStateSource, connectionCommands: prepared.Connections, resumeListening: prepared.ResumeListening), prepared);
                    async ValueTask PublishReplacement(CancellationToken cancellation)
                    {
                        // ActiveConfigurationTransition performs storage work away from the UI.
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            handedOff = true;
                            return sessions.ReplaceAsync(replacement, cancellation);
                        });
                    }
                    if (library is IActiveConfigurationService active)
                        await new ActiveConfigurationTransition(active).PublishAsync(configuration, PublishReplacement,
                            () => ReferenceEquals(sessions.Active, replacement), token);
                    else
                        await PublishReplacement(token);
                }
                catch (Exception failure)
                {
                    if (!handedOff)
                        await ConsoleSessionConstruction.RollbackAsync(failure,
                            replacement is not null ? replacement.DisposeOwnerAsync : prepared.Application.DisposeAsync);
                    throw;
                }
            }
            if (CreateConfigurationLibrary is { } createLibrary)
            {
                configurationView = new MobileConfigurationView(createLibrary, CreateExportArchive,
                    CreateSession is null ? null : OpenConsole, CreateStudio, CreateDraftStudio,
                    () => ((MobileSessionAttachment)sessions.Active).Session, formFactor);
                settings = new MobileSettingsView(
                    configurationView,
                    formFactor, () => console, () => ((MobileSessionAttachment)sessions.Active).Session,
                    CreateAudioRoutePicker?.Invoke(), CreateHelpCatalog, CreateAudioInputSettings?.Invoke());
                settings.ConsoleRequested += (_, _) => Select(true);
            }
            sessions.ReplacementFollowUpFailed += (_, failure) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Cleanup can finish after another configuration has been opened.
                    // Never show a retired session's warning over the current session.
                    if (!ReferenceEquals(sessions.Active, failure.Active) || settings is null) return;
                    settings.ShowSessionFollowUpFailure(failure.Phase, failure.Exception.Message);
                    if (failure.Active is MobileSessionAttachment attachment)
                        attachment.Session.Diagnostics?.Add(DateTimeOffset.Now, "Session",
                            DvmConsole.Core.Diagnostics.DebugLogSeverity.Error, failure.Exception.Message);
                    Select(false);
                });
            Control content = page;
            // A Border applies safe-area padding to the whole shell, including
            // navigation. TabControl's header sits outside its content padding.
            var safeArea = new Border { Child = new MobileInputPaneView(content) };
            safeArea.Resources.MergedDictionaries.Add(new MobileConsoleTheme());
            safeArea.Bind(Border.BackgroundProperty, safeArea.GetResourceObservable("ShellBackgroundBrush"));
            TopLevel.SetAutoSafeAreaPadding(safeArea, true);
            lifetime.MainView = safeArea;
            if (RestoreSavedSessionOnStartup && configurationView is not null && CreateSession is not null)
            {
                bool startupAttempted = false;
                safeArea.Loaded += async (_, _) =>
                {
                    if (startupAttempted) return;
                    startupAttempted = true;
                    // Do not allow a manual open to race the saved-session replacement.
                    page.IsEnabled = false;
                    try { await configurationView.OpenActiveAsync(); }
                    catch (Exception exception)
                    {
                        settings!.ShowStartupFailure(exception.Message);
                        Select(false);
                    }
                    finally { page.IsEnabled = true; }
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
