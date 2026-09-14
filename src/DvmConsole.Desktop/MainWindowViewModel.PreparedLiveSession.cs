// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using DvmConsole.Storage;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    /// <summary>Host resources shared by pre-view construction and the attached shell.</summary>
    internal sealed partial class PreparedLiveSession
    {
        private readonly ConsoleOperationalRuntime? runtime;
        private readonly ConsoleSessionServices? sessionServices;
        private readonly ConsoleSessionState? state;
        private readonly UserSettings? settings;
        private ApplicationAudioBackendProvider? audio;
        private CallRecordingManager? recordings;
        private readonly SemaphoreSlim? pttGate;
        private readonly SemaphoreSlim? transmitGate;
        private readonly SemaphoreSlim? audioGate;
        private readonly object? receiveSync;
        private RecordingPlaybackCoordinator? recordingPlayback;
        private WebStreamPlaybackCoordinator? webPlayback;

        private MainWindowViewModel? presentation;
        public MainWindowViewModel? ViewModel { get; private set; }
        public MainWindowViewModel? Presentation => Volatile.Read(ref presentation);
        public void CompleteAttachment()
        {
            Volatile.Write(ref presentation,
                ViewModel ?? throw new InvalidOperationException("Attach a shell before publishing its presentation."));
            ProjectPreparedHistory();
        }
        public ConsoleOperationalRuntime Runtime => runtime ?? ViewModel!.operationalRuntime;
        public ConsoleSessionState? State => state ?? ViewModel?.PreparedState;
        public UserSettings Settings => settings ?? ViewModel!.userSettings;
        public ApplicationAudioBackendProvider? OwnedAudioBackendProvider => audio ?? ViewModel?.audioBackendProvider;
        public CallRecordingManager? OwnedRecordings => recordings ?? ViewModel?.callRecordings;
        public ApplicationAudioBackendProvider AudioBackendProvider => OwnedAudioBackendProvider!;
        public CallRecordingManager Recordings => (recordings ?? ViewModel?.callRecordings)!;
        public SemaphoreSlim PttStateChangeLock => pttGate ?? ViewModel!.pttStateChangeLock;
        public SemaphoreSlim TransmitAdmissionGate => transmitGate ?? ViewModel!.transmitAdmissionGate;
        public SemaphoreSlim AudioReconfigurationLock => audioGate ?? ViewModel!.audioReconfigurationLock;
        public object ReceiveSync => receiveSync ?? ViewModel!.receiveLifecycleSync;
        public RecordingPlaybackCoordinator? RecordingPlayback => recordingPlayback ?? ViewModel?.recordingPlayback;
        public WebStreamPlaybackCoordinator? WebPlayback => webPlayback;

        private PreparedLiveSession(MainWindowViewModel viewModel)
        {
            ViewModel = viewModel;
            audioPolicy = new(viewModel.userSettings);
        }
        public static PreparedLiveSession FromViewModel(MainWindowViewModel viewModel) => new(viewModel);

        public PreparedLiveSession(ConsoleOperationalRuntime runtime, ConsoleSessionState state,
            UserSettings settings, DesktopRuntimeDependencies dependencies, ConsoleSessionServices services)
        {
            this.runtime = runtime;
            sessionServices = services;
            this.state = state;
            this.settings = settings;
            audioPolicy = new(settings);
            pttGate = new(1, 1);
            transmitGate = new(1, 1);
            audioGate = new(1, 1);
            receiveSync = new();
        }

        private void InitializeAudioBackend(DesktopRuntimeDependencies dependencies)
        {
            audio = new(AudioPolicy.BackendConfiguration,
                configuration => dependencies.AudioBackendFactory.Create(new(configuration.ProcessingMode,
                    configuration.InputDeviceId, configuration.OutputDeviceId)));
        }

        private void InitializeRecordingStore(DesktopRuntimeDependencies dependencies)
        {
            var settings = Settings;
            var runtime = Runtime;
            var state = State!;
            string root = AudioPolicy.RecordingRoot(dependencies.UserSettingsStore.Path);
            recordings = new(root, (channel, failure) =>
            {
                state.Status.SetAudio($"Recording failed on {runtime.Media.State(channel).Runtime.Definition.Name}: {failure.Message}");
                if (Presentation is { } view) view.HandleRecordingFaulted(view.ResolveChannel(channel), failure);
            }, settings.RecordingRetentionDays, runtime.Media.ShouldRecord, runtime.Media.SubscriberAlias);
        }


        public void Attach(MainWindowViewModel viewModel)
        {
            if (ViewModel is not null && !ReferenceEquals(ViewModel, viewModel))
                throw new InvalidOperationException("Prepared runtime is already attached to a shell.");
            ViewModel = viewModel;
        }
    }
}
