// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using AVFoundation;
using Foundation;

namespace DvmConsole.Audio;

public enum IosAudioExecutionState { Idle, Listening, Capturing, Interrupted, RequiresResume, Disposed }
public sealed record IosAudioSessionChange(IosAudioExecutionState State, string Reason, bool ManualTransmitMustStop, int? RecoveryGeneration = null);
internal readonly record struct IosAudioDeviceVersion(int Generation, int SampleRate);

/// <summary>One host-owned AVAudioSession and RemoteIO lifetime for the application.</summary>
public sealed class IosAudioSessionOwner : IDisposable
{
    private readonly object sync = new();
    private readonly AVAudioSession session = AVAudioSession.SharedInstance();
    private readonly List<NSObject> observers = [];
    private RemoteIoDevice? device;
    private Task retirement = Task.CompletedTask;
    private int generation;
    private int sampleRate;
    private bool outputRequested;
    private bool listeningRequired = true;
    private bool inputActive;
    private object? captureOwner;
    private string? preferredInputId;
    private int? inputSelectionGeneration;
    private bool disposed;
    private IosAudioExecutionState state;

    public IosAudioSessionOwner()
    {
        observers.Add(AVAudioSession.Notifications.ObserveInterruption((_, args) =>
            HandleInterruption(args.InterruptionType == AVAudioSessionInterruptionType.Began,
                args.InterruptionType != AVAudioSessionInterruptionType.Began &&
                (args.Option & AVAudioSessionInterruptionOptions.ShouldResume) != 0)));
        observers.Add(AVAudioSession.Notifications.ObserveMediaServicesWereLost((_, _) =>
            Pause(IosAudioExecutionState.Interrupted, "Audio services unavailable.")));
        observers.Add(AVAudioSession.Notifications.ObserveMediaServicesWereReset((_, _) =>
            Pause(IosAudioExecutionState.RequiresResume, "Audio services reset. Resume listening explicitly.")));
        observers.Add(AVAudioSession.Notifications.ObserveRouteChange((_, args) =>
        {
            if (args.Reason == AVAudioSessionRouteChangeReason.CategoryChange) return;
            Pause(IosAudioExecutionState.RequiresResume,
                args.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable
                    ? "Audio route removed. Transmit stopped; select a route and resume listening."
                    : "Audio route changed. Resume listening on the new route.");
        }));
    }

    internal void HandleInterruption(bool began, bool shouldResume)
    {
        if (began)
        {
            Pause(IosAudioExecutionState.Interrupted, "Audio interrupted.");
            return;
        }
        IosAudioSessionChange change;
        lock (sync)
        {
            if (disposed) return;
            int? recovery = shouldResume && state == IosAudioExecutionState.Interrupted ? generation : null;
            change = new(state, recovery.HasValue ? "Audio interruption ended. Resuming listening."
                : "Audio interruption ended. Listening can be resumed.", true, recovery);
        }
        Publish(change);
    }

    public event EventHandler<IosAudioSessionChange>? Changed;
    public IosAudioExecutionState State { get { lock (sync) return state; } }
    public IReadOnlyList<AudioDeviceInfo> AvailableInputs => (session.AvailableInputs ?? [])
        .Select(port => new AudioDeviceInfo(port.UID, port.PortName, AudioDirection.Input,
            session.CurrentRoute.Inputs.Any(input => input.UID == port.UID))).ToArray();
    public string? GetRouteIdentity(AudioDirection direction)
    {
        AVAudioSessionPortDescription[] ports = direction == AudioDirection.Input
            ? session.CurrentRoute.Inputs : session.CurrentRoute.Outputs;
        return ports.Length == 0 ? null : string.Join("\u001F", ports.Select(port => port.UID));
    }
    public bool? IsBluetoothRoute(AudioDirection direction)
    {
        lock (sync)
        {
            if (direction == AudioDirection.Input && preferredInputId is { } selectedId)
            {
                var selected = (session.AvailableInputs ?? []).FirstOrDefault(port => port.UID == selectedId);
                return selected is null ? null : IsBluetoothPort(selected);
            }
            var ports = direction == AudioDirection.Input ? session.CurrentRoute.Inputs : session.CurrentRoute.Outputs;
            if (ports.Length > 0) return ports.Any(IsBluetoothPort);
            // Playback-only sessions have no input port yet. A built-in output
            // with no explicit microphone selection is an ordinary local start.
            var outputs = session.CurrentRoute.Outputs;
            if (direction == AudioDirection.Input && outputs.Length > 0 && outputs.All(port =>
                port.PortType == AVAudioSession.PortBuiltInSpeaker || port.PortType == AVAudioSession.PortBuiltInReceiver))
                return false;
            return null;
        }
    }

    private static bool IsBluetoothPort(AVAudioSessionPortDescription port)
        => port.PortType == AVAudioSession.PortBluetoothHfp || port.PortType == AVAudioSession.PortBluetoothA2DP ||
            port.PortType == AVAudioSession.PortBluetoothLE;

    public string OutputName => string.Join(", ", session.CurrentRoute.Outputs.Select(port => port.PortName));

    internal void AcquireOutput()
    {
        lock (sync)
        {
            EnsureAvailable();
            if (outputRequested) throw new InvalidOperationException("iOS supports one physical output mix.");
            if (listeningRequired || inputActive) EnsureUnit(inputActive);
            outputRequested = true;
            device?.SetOutputEnabled(true);
        }
    }

    /// <summary>Releases idle hardware without discarding the mix or requiring an operator resume.</summary>
    public void SetListeningRequired(bool required)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (listeningRequired == required) return;
            listeningRequired = required;
            if (inputActive) return;
            if (required)
            {
                if (outputRequested && state is not (IosAudioExecutionState.Interrupted or IosAudioExecutionState.RequiresResume))
                    EnsureUnit(input: false);
            }
            else
            {
                RetireUnit();
                var previous = state;
                Deactivate();
                // Disconnecting must not clear an explicit pause or interruption.
                if (previous is IosAudioExecutionState.Interrupted or IosAudioExecutionState.RequiresResume)
                    state = previous;
            }
        }
    }

    internal void ReleaseOutput()
    {
        lock (sync)
        {
            outputRequested = false;
            if (disposed) return;
            if (inputActive)
                device?.SetOutputEnabled(false);
            else
            {
                RetireUnit();
                if (state is not (IosAudioExecutionState.Interrupted or IosAudioExecutionState.RequiresResume)) Deactivate();
            }
        }
    }

    internal async Task StartCaptureAsync(object requester, CancellationToken cancellationToken)
    {
        int requestedGeneration;
        lock (sync)
        {
            EnsureAvailable();
            if (captureOwner is not null) throw new InvalidOperationException("The microphone is already in use.");
            requestedGeneration = generation;
            captureOwner = requester;
        }
        bool started = false;
        try
        {
            if (!await RequestPermissionAsync(cancellationToken).ConfigureAwait(false))
                throw new UnauthorizedAccessException("Microphone access was denied. Listening remains available.");
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                EnsureAvailable();
                if (generation != requestedGeneration || !ReferenceEquals(captureOwner, requester))
                    throw new OperationCanceledException("The audio route changed while requesting microphone access.");
                RetireUnit();
                try { EnsureUnit(input: true); inputActive = true; started = true; }
                catch (Exception startupFailure)
                {
                    try { if (outputRequested && listeningRequired) EnsureUnit(input: false); else Deactivate(); }
                    catch (Exception recoveryFailure) { throw new AggregateException(startupFailure, recoveryFailure); }
                    throw;
                }
            }
        }
        finally
        {
            if (!started)
                lock (sync) { if (ReferenceEquals(captureOwner, requester)) captureOwner = null; }
        }
    }

    public bool? MicrophonePermissionGranted
    {
        get
        {
            if (OperatingSystem.IsIOSVersionAtLeast(17) || OperatingSystem.IsMacCatalystVersionAtLeast(17))
                return AVAudioApplication.SharedInstance.RecordPermission switch
                {
                    AVAudioApplicationRecordPermission.Granted => true,
                    AVAudioApplicationRecordPermission.Denied => false,
                    _ => null
                };
            return session.RecordPermission switch
            {
                AVAudioSessionRecordPermission.Granted => true,
                AVAudioSessionRecordPermission.Denied => false,
                _ => null
            };
        }
    }

    public async Task<bool> RequestPermissionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsIOSVersionAtLeast(17) || OperatingSystem.IsMacCatalystVersionAtLeast(17))
            return await AVAudioApplication.RequestRecordPermissionAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RequestRecordPermission(granted => permission.TrySetResult(granted));
        return await permission.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void StopCapture(object requester)
    {
        lock (sync)
        {
            if (!ReferenceEquals(captureOwner, requester)) return;
            captureOwner = null;
            if (!inputActive) { generation++; return; }
            inputActive = false;
            RetireUnit();
            if (disposed) return;
            if (outputRequested && listeningRequired && state is not (IosAudioExecutionState.Interrupted or IosAudioExecutionState.RequiresResume))
                EnsureUnit(input: false);
            else if (!outputRequested || !listeningRequired) Deactivate();
        }
    }

    /// <summary>Recovery restores output only. It never reacquires microphone intent.</summary>
    public Task ResumeListeningAsync(CancellationToken cancellationToken = default)
        => ResumeListeningCoreAsync(null, cancellationToken);

    public Task ResumeInterruptedListeningAsync(int recoveryGeneration, CancellationToken cancellationToken = default)
        => ResumeListeningCoreAsync(recoveryGeneration, cancellationToken);

    private async Task ResumeListeningCoreAsync(int? recoveryGeneration, CancellationToken cancellationToken)
    {
        Task pending;
        int requestedGeneration;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CheckAutomaticRecovery(recoveryGeneration);
            pending = retirement;
            requestedGeneration = generation;
        }
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            CheckAutomaticRecovery(recoveryGeneration);
            if (state is IosAudioExecutionState.Listening or IosAudioExecutionState.Capturing) return;
            if (generation != requestedGeneration || !ReferenceEquals(pending, retirement))
                throw new OperationCanceledException("Another audio transition replaced this recovery.");
            inputActive = false;
            captureOwner = null;
            IosAudioExecutionState previous = state;
            state = IosAudioExecutionState.Idle;
            try { if (outputRequested && listeningRequired) EnsureUnit(input: false); }
            catch
            {
                if (state == IosAudioExecutionState.Idle) state = previous;
                throw;
            }
        }
        Publish(new(State, "Listening resumed.", true));
    }

    private void CheckAutomaticRecovery(int? expectedGeneration)
    {
        if (expectedGeneration is { } expected &&
            (state != IosAudioExecutionState.Interrupted || generation != expected))
            throw new OperationCanceledException("This interruption recovery was superseded.");
    }

    /// <summary>Enumerates input routes on explicit request without starting capture or transmission.</summary>
    public async Task<IReadOnlyList<AudioDeviceInfo>> PrepareInputSelectionAsync(CancellationToken cancellationToken)
    {
        int requestedGeneration;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            requestedGeneration = generation;
        }
        if (!await RequestPermissionAsync(cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Microphone access was denied. Listening remains available.");
        cancellationToken.ThrowIfCancellationRequested();
        Pause(IosAudioExecutionState.RequiresResume,
            "Choosing a microphone. Transmit stopped; resume listening when ready.", expectedGeneration: requestedGeneration);
        requestedGeneration++;
        Task pending;
        lock (sync) pending = retirement;
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (generation != requestedGeneration || state != IosAudioExecutionState.RequiresResume)
                throw new OperationCanceledException("Input selection was interrupted. Choose the microphone again.");
            // Playback category exposes no inputs. Activate input capability only after
            // the explicit request and permission, without creating a RemoteIO unit.
            ConfigureAudioSession(input: true);
            if (generation != requestedGeneration)
                throw new OperationCanceledException("The audio route changed. Refresh the microphone choices.");
            inputSelectionGeneration = generation;
            return AvailableInputs;
        }
    }

    public string? PreferredInputId { get { lock (sync) return preferredInputId; } }

    /// <summary>Restores a host preference before activating a replacement session.</summary>
    public void RestoreInputPreference(string? inputId)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (captureOwner is not null)
                throw new InvalidOperationException("Release the microphone before restoring input settings.");
            preferredInputId = inputId;
            inputSelectionGeneration = null;
        }
    }

    public void SelectInput(string? inputId)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (state != IosAudioExecutionState.RequiresResume || inputSelectionGeneration != generation)
                throw new InvalidOperationException("Refresh the microphone choices before selecting an input.");
            if (inputId is not null && !(session.AvailableInputs ?? []).Any(port => port.UID == inputId))
                throw new ArgumentException("The selected input is unavailable. Refresh the microphone choices.", nameof(inputId));
            // Apply at the next explicit capture after its category is active. Never
            // silently substitute a different microphone when this route disappears.
            preferredInputId = inputId;
        }
    }

    internal IosAudioDeviceVersion Version
    {
        get { lock (sync) { EnsureAvailable(); return new(generation, sampleRate); } }
    }
    internal bool TryGetPlaybackVersion(out IosAudioDeviceVersion version)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            version = new(generation, sampleRate);
            return device is not null && state is not (IosAudioExecutionState.Interrupted or IosAudioExecutionState.RequiresResume);
        }
    }
    internal int Write(IosAudioDeviceVersion version, ReadOnlySpan<short> samples)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Suppression can race a write already handed to the physical output.
            // Drop that PCM instead of reporting an endpoint fault or replaying it
            // after recovery. Capture still uses strict generation validation.
            if (state is IosAudioExecutionState.Interrupted or IosAudioExecutionState.RequiresResume ||
                device is null || generation != version.Generation)
                return samples.Length;
            EnsureVersion(version);
            return device!.Write(samples);
        }
    }
    internal int Read(IosAudioDeviceVersion version, Span<short> samples)
    {
        lock (sync) { EnsureVersion(version); return device!.Read(samples); }
    }
    internal RemoteIoDevice.CaptureSignal AcquireCaptureSignal(IosAudioDeviceVersion version, CancellationToken token)
    {
        lock (sync)
        {
            EnsureVersion(version);
            return new RemoteIoDevice.CaptureSignal(device!, token);
        }
    }
    internal int QueuedOutput { get { lock (sync) return checked((int)(device?.QueuedOutput ?? 0)); } }
    internal long CallbackCount { get { lock (sync) return checked((long)(device?.CallbackCount ?? 0)); } }

    public IosAudioDeviceDiagnostics CaptureDiagnostics()
    {
        lock (sync)
            return new(generation, sampleRate, device?.CallbackCount ?? 0, device?.DroppedInputSamples ?? 0,
                device?.StarvedSamples ?? 0, device?.PendingStarvedSamples ?? 0);
    }

    internal void EndExpectedPlayback()
    {
        lock (sync) device?.EndExpectedPlayback();
    }

    public void StopImmediately() => Pause(IosAudioExecutionState.RequiresResume, "Audio stopped.");

    internal void StopCaptureImmediately(object requester)
        => Pause(IosAudioExecutionState.RequiresResume, "Microphone stopped.", requester);

    private void Pause(IosAudioExecutionState next, string reason, object? captureRequester = null, int? expectedGeneration = null)
    {
        lock (sync)
        {
            if (expectedGeneration is { } expected)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (generation != expected)
                    throw new OperationCanceledException("The audio session changed. Choose the microphone again.");
            }
            if (disposed || (captureRequester is not null && !ReferenceEquals(captureOwner, captureRequester))) return;
            // A later interruption cannot relax a reset, route-loss or explicit-stop
            // requirement. Only the operator's resume action may leave this state.
            if (state == IosAudioExecutionState.RequiresResume && next == IosAudioExecutionState.Interrupted) return;
            state = next;
            inputActive = false;
            captureOwner = null;
            generation++;
            RemoteIoDevice? retired = device;
            device = null;
            if (retired is not null)
            {
                retired.StopImmediately();
                Task previous = retirement;
                retirement = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); }
                    finally { retired.Dispose(); }
                });
            }
        }
        // State and generation close admission synchronously. Native retirement must
        // not wait for observers: an observer may dispose this owner during notification.
        Publish(new(next, reason, true));
    }

    private void EnsureUnit(bool input)
    {
        EnsureAvailable();
        if (device is not null) return;
        int expectedGeneration = generation;
        ConfigureAudioSession(input);
        if (input)
        {
            AVAudioSessionPortDescription? selected = null;
            if (preferredInputId is { } inputId)
                selected = (session.AvailableInputs ?? []).FirstOrDefault(port => port.UID == inputId)
                    ?? throw new IOException("The selected microphone is unavailable. Choose a microphone in Settings before transmitting.");
            Check(session.SetPreferredInput(selected, out NSError? inputError), inputError);
        }
        EnsureAvailable();
        if (generation != expectedGeneration) throw new OperationCanceledException("Audio activation was interrupted.");
        sampleRate = checked((int)Math.Round(session.SampleRate));
        RemoteIoDevice created = RemoteIoDevice.Create(sampleRate, input);
        try { created.SetOutputEnabled(outputRequested || !input); created.Start(); }
        catch { created.Dispose(); throw; }
        device = created;
        generation++;
        state = input ? IosAudioExecutionState.Capturing : IosAudioExecutionState.Listening;
    }

    private void ConfigureAudioSession(bool input)
    {
        // System playback controls require a primary, non-mixing audio session.
        AVAudioSessionCategoryOptions options = default;
        if (input) options |= AVAudioSessionCategoryOptions.DefaultToSpeaker | AVAudioSessionCategoryOptions.AllowBluetooth | AVAudioSessionCategoryOptions.AllowBluetoothA2DP;
        NSError? categoryError = session.SetCategory(input ? AVAudioSessionCategory.PlayAndRecord : AVAudioSessionCategory.Playback, options);
        Check(categoryError is null, categoryError);
        // RemoteIO carries radio PCM and generated tones; Console owns microphone DSP.
        // VoiceChat changes routing/EQ and reduces playback level without Voice Processing I/O.
        // Keep Default for both listening and capture rather than layering chat processing on it.
        Check(session.SetMode(AVAudioSessionMode.Default.GetConstant()!, out NSError? error), error);
        Check(session.SetActive(true, out error), error);
    }

    private void RetireUnit()
    {
        device?.StopImmediately();
        device?.Dispose();
        device = null;
        generation++;
    }
    private void Deactivate()
    {
        Check(session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out NSError? error), error);
        state = IosAudioExecutionState.Idle;
    }
    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (state is IosAudioExecutionState.Interrupted or IosAudioExecutionState.RequiresResume)
            throw new InvalidOperationException("Audio is paused. Resume listening before starting another operation.");
    }
    private void EnsureVersion(IosAudioDeviceVersion version)
    {
        EnsureAvailable();
        if (device is null || generation != version.Generation) throw new IOException("The audio endpoint changed.");
        if (device.Error != 0) throw new IOException($"RemoteIO callback failed ({device.Error}).");
    }
    private static void Check(bool success, NSError? error)
    {
        if (success) { error?.Dispose(); return; }
        string reason = error?.LocalizedDescription ?? "The system audio operation failed.";
        error?.Dispose();
        throw new IOException(reason);
    }
    private void Publish(IosAudioSessionChange change)
    {
        foreach (EventHandler<IosAudioSessionChange> handler in Changed?.GetInvocationList() ?? [])
        {
            try { handler(this, change); }
            catch (Exception exception) { System.Diagnostics.Trace.TraceError("Audio lifecycle observer failed: {0}", exception); }
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            state = IosAudioExecutionState.Disposed;
            inputActive = false;
            outputRequested = false;
            RetireUnit();
        }
        retirement.GetAwaiter().GetResult();
        foreach (NSObject observer in observers) observer.Dispose();
        observers.Clear();
        session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out NSError? error);
        error?.Dispose();
    }
}
