// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using DvmConsole.Mobile;
using DvmConsole.Storage;
using DvmConsole.Vocoder;

namespace DvmConsole.iOS;

/// <summary>Application-lifetime iOS services, shared across configuration replacements.</summary>
internal sealed class IosConsoleHost : IApplicationLifecycle, IMicrophonePermissionService
{
    private readonly string root = IosConfigurationStorage.OpenRoot();
    private readonly IosAudioSessionOwner audio = new();
    private readonly IosInputPreferences inputPreferences = new();
    private InputConfiguration? inputConfiguration;
    private sealed record InputConfiguration(ConsoleReceiveSession Runtime, ConfigurationId Id);

    internal IosInputSelectionContext CreateInputSelectionContext()
    {
        var binding = Volatile.Read(ref inputConfiguration)
            ?? throw new InvalidOperationException("Open a configuration before choosing its microphone.");
        return new(audio, inputId =>
        {
            if (!ReferenceEquals(binding, Volatile.Read(ref inputConfiguration)))
                throw new InvalidOperationException("The configuration changed. Refresh the microphone choices.");
            audio.SelectInput(inputId);
            inputPreferences.Write(binding.Id, inputId);
        });
    }
    private IosListeningControls? listeningControls;
    private readonly IosBackgroundCheckpoint checkpoints = new();
    private ConsoleReceiveSession? activeRuntime;
    private OpusRecordingStore? recordings;
    private ManagedReceivePreferences? preferences;
    public bool IsActive { get; private set; } = true;
    public event EventHandler? Activated;
    public event EventHandler? Deactivated;
    public event EventHandler? Suspending { add { } remove { } }
    public event EventHandler? Resumed { add { } remove { } }
    public event EventHandler? Stopping { add { } remove { } }

    public void SetForeground(bool foreground)
    {
        if (IsActive == foreground) return;
        IsActive = foreground;
        if (foreground)
        {
            Activated?.Invoke(this, EventArgs.Empty);
            _ = listeningControls?.ResumeOnForegroundAsync();
        }
        else
        {
            Deactivated?.Invoke(this, EventArgs.Empty);
            CheckpointActiveSession();
        }
    }

    private void CheckpointActiveSession()
    {
        if (Volatile.Read(ref activeRuntime) is { } runtime)
            DvmConsole.Threading.TaskObservation.Observe(checkpoints.RunAsync(token => runtime.CheckpointAsync(token).AsTask()));
    }

    public ValueTask<MicrophonePermissionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(audio.MicrophonePermissionGranted switch
        {
            true => MicrophonePermissionState.Granted,
            false => MicrophonePermissionState.Denied,
            null => MicrophonePermissionState.Unknown
        });
    }
    public async ValueTask<MicrophonePermissionState> RequestAsync(CancellationToken cancellationToken = default)
        => await audio.RequestPermissionAsync(cancellationToken).ConfigureAwait(false)
            ? MicrophonePermissionState.Granted : MicrophonePermissionState.Denied;

    public async ValueTask<MobileSession> CreateAsync(IConfigurationLibrary library,
        ConfigurationReference reference, CancellationToken cancellationToken)
    {
        // Check protection before importing key material or constructing endpoints.
        _ = IosConfigurationStorage.OpenRoot();
        IConfigurationMaterializationLease lease = await new ManagedConfigurationMaterializer(library,
            Path.Combine(root, "Runtime")).MaterializeAsync(reference, cancellationToken);
        bool transferred = false;
        try
        {
            ConsoleSessionLoadResult loaded = ConfigurationSessionLoader.Load(lease.Path, reference);
            if (loaded.Topology is not { IsValid: true } topology)
                throw new InvalidDataException(loaded.StatusText);
            ConsoleSessionServices? ownership = null;
            IAssetStore? toneAssets = null;
            ConsoleReceiveSession runtime = await ConsoleReceiveSession.CreateAsync(topology.Configuration, (state, services) =>
            {
                ownership = services;
                services.Connection.OwnAsync("configuration-materialization", lease);
                transferred = true;
                var keys = ConfigurationKeyRingLoader.Load(topology.Configuration, out string? warning);
                services.Connection.Own("p25-keys", keys.P25);
                services.Connection.Own("dmr-keys", keys.Dmr);
                services.Connection.Own("nxdn-keys", keys.Nxdn);
                ConsoleRadioSessionPlan radios = FneConsoleRadioSessions.Prepare(state,
                    topology.Configuration.Systems.Select(FneConnectionOptions.FromConfiguration),
                    channel => new ChannelConfigurationAccess(channel.Runtime.Definition, keys.P25, keys.Dmr, keys.Nxdn));
                recordings ??= new OpusRecordingStore(Path.Combine(root, "Recordings"), null, CallRecordingManager.DefaultRetentionDays);
                var capture = services.Recording.OwnAsync("receive-capture", new CallRecordingManager(recordings,
                    shouldRecordSource: state.Media.ShouldRecord,
                    resolveSubscriberAlias: state.Media.SubscriberAlias));
                var host = new ConsoleHostServices(radios, new IosAudioBackendFactory(audio),
                    new NativeVocoderFactory(NativeVocoderLinkage.StaticallyLinked), library,
                    toneAssets = new ManagedAssetStore(Path.Combine(root, "Assets")), recordings, this,
                    SystemClock.Instance, new BackgroundApplicationScheduler(exception => Console.Error.WriteLine(exception)), SystemApplicationDelay.Instance, this, []);
                preferences ??= new ManagedReceivePreferences(Path.Combine(root, "UserSettings.json"));
                var receivePreferences = preferences.ForConfiguration(reference.Id,
                    state.Channels.ToDictionary(pair => pair.Key,
                        pair => $"{pair.Value.Runtime.Definition.SystemName}\u001F{pair.Value.Runtime.Definition.Name}"));
                return new(host, radios.Systems, FneReceiveFrameNormalization.Instance, keys.P25, keys.Dmr, keys.Nxdn,
                    InitialStatus: warning, Recordings: capture, Preferences: receivePreferences,
                    ManualInput: new AudioInputProcessingOptions());
            }, reference, cancellationToken);
            void AudioChanged(object? sender, IosAudioSessionChange change)
            {
                if (!change.RecoveryGeneration.HasValue && change.State == audio.State &&
                    change.State is IosAudioExecutionState.Interrupted or IosAudioExecutionState.RequiresResume)
                    runtime.SetAudioAvailable(false, change.Reason + (change.RecoveryGeneration.HasValue ? "" : " Open Settings to resume listening."),
                        requiresExplicitResume: change.State == IosAudioExecutionState.RequiresResume);
                if (change.RecoveryGeneration is { } generation)
                    _ = RecoverListeningAsync(generation);
            }
            async Task RecoverListeningAsync(int generation)
            {
                try
                {
                    // The runtime owns cancellation after construction has completed.
                    await runtime.ResumeAudioAsync(token => audio.ResumeInterruptedListeningAsync(generation, token), CancellationToken.None);
                }
                catch (OperationCanceledException) { /* A newer interruption, route change or retirement owns recovery. */ }
                catch (ObjectDisposedException) { /* The prepared or outgoing session was retired. */ }
                catch (Exception exception)
                {
                    // The runtime reports failure only while this attempt still owns recovery.
                    Console.Error.WriteLine($"Listening recovery failed: {exception}");
                }
            }
            audio.Changed += AudioChanged;
            try
            {
                runtime.LocalToneMonitorEnabled = (await preferences!.LoadToneSettingsAsync(cancellationToken)).LocalToneMonitorEnabled;
                ownership!.Presentation.Register("ios-audio-observer", () =>
                { audio.Changed -= AudioChanged; return ValueTask.CompletedTask; });
                listeningControls ??= new IosListeningControls(audio);
                var controls = listeningControls;
                ownership.Presentation.Register("ios-listening-controls", () => new ValueTask(controls.UnbindAsync(runtime)));
                ownership.Presentation.Register("ios-checkpoint-binding", () =>
                {
                    Interlocked.CompareExchange(ref activeRuntime, null, runtime);
                    var binding = Volatile.Read(ref inputConfiguration);
                    if (binding?.Runtime == runtime)
                        Interlocked.CompareExchange(ref inputConfiguration, null, binding);
                    return ValueTask.CompletedTask;
                });
                var connectionStartup = (IConsoleConnectionStartupPreferences)preferences.ForConfiguration(reference.Id,
                    new Dictionary<ChannelId, string>());
                bool startupConnectionsApplied = false;
                async Task ActivateListeningAsync(CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    audio.RestoreInputPreference(inputPreferences.Read(reference.Id));
                    await runtime.ActivateListeningAsync(token);
                    token.ThrowIfCancellationRequested();
                    await controls.BindAsync(runtime, token);
                    token.ThrowIfCancellationRequested();
                    Volatile.Write(ref activeRuntime, runtime);
                    Volatile.Write(ref inputConfiguration, new(runtime, reference.Id));
                    if (!startupConnectionsApplied)
                    {
                        startupConnectionsApplied = true;
                        if (await connectionStartup.LoadAutoConnectAsync(token)) await runtime.ConnectAsync(token);
                    }
                    if (!IsActive) CheckpointActiveSession();
                }
                var facade = new ConsoleApplicationSession(runtime);
                return new(facade, runtime, runtime.CaptureActiveSystemIds,
                    token => runtime.ResumeAudioAsync(audio.ResumeListeningAsync, token), ActivateListeningAsync,
                    (destination, token) =>
                    {
                        var rows = runtime.History.Select(CallHistoryExportRow.FromCall).ToArray();
                        return Task.Run(() =>
                        {
                            token.ThrowIfCancellationRequested();
                            CallHistoryCsv.Write(destination, rows, leaveOpen: true);
                        }, token);
                    }, runtime.CreateRecordingArchive(new OpusRecordingArchive(recordings!))) { Execution = runtime.Execution, ToneSettings = preferences, Assets = toneAssets, ConnectionStartup = connectionStartup };
            }
            catch (Exception failure)
            {
                audio.Changed -= AudioChanged;
                await ConsoleSessionConstruction.RollbackAsync(failure, runtime.DisposeAsync);
                throw;
            }
        }
        catch (Exception failure)
        {
            if (!transferred)
                await ConsoleSessionConstruction.RollbackAsync(failure, lease.DisposeAsync);
            throw;
        }
    }
}
