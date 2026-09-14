// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Audio;

/// <summary>Silent output qualification. Never requests microphone permission.</summary>
public static class IosAudioDiagnostics
{
    public static async Task<string> RunPlaybackAsync(CancellationToken cancellationToken = default)
    {
        using var owner = new IosAudioSessionOwner();
        using var backend = new IosAudioBackend(owner);
        AudioDeviceInfo route = backend.EnumerateDevices(AudioDirection.Output).Single();
        IAudioPlayback first = backend.OpenPlayback(route, PcmAudioFormat.Voice8KhzStereo16Bit);
        try
        {
            await ExerciseAsync(first, cancellationToken).ConfigureAwait(false);
            if ((AVFoundation.AVAudioSession.SharedInstance().CategoryOptions &
                 AVFoundation.AVAudioSessionCategoryOptions.MixWithOthers) != 0)
                throw new InvalidOperationException("Listening is not eligible for primary system playback controls.");
            await ExerciseContinuityAsync(first, cancellationToken).ConfigureAwait(false);
            if (owner.CaptureDiagnostics().DroppedInputSamples != 0)
                throw new InvalidOperationException("Listening-only diagnostic reported capture drops.");
            owner.SetListeningRequired(false);
            await first.WriteAsync(new short[320], cancellationToken).ConfigureAwait(false);
            if (owner.State != IosAudioExecutionState.Idle || owner.CallbackCount != 0 || first.QueuedSamples != 0)
                throw new InvalidOperationException("Stopped sources retained active output.");
            owner.SetListeningRequired(true);
            await ExerciseAsync(first, cancellationToken).ConfigureAwait(false);
            owner.StopImmediately();
            owner.SetListeningRequired(false);
            owner.SetListeningRequired(true);
            if (owner.State != IosAudioExecutionState.RequiresResume)
                throw new InvalidOperationException("Source restart bypassed the operator's audio pause.");
            await owner.ResumeListeningAsync(cancellationToken).ConfigureAwait(false);
            await ExerciseInterruptionAsync(owner, first, cancellationToken).ConfigureAwait(false);
            IosAudioDeviceVersion outgoingVersion = owner.Version;
            owner.StopImmediately();
            if (owner.State != IosAudioExecutionState.RequiresResume)
                throw new InvalidOperationException("Immediate stop did not close audio admission.");
            // A mixer write can already be in flight when listening is paused.
            // It must be discarded without faulting the selected receive route.
            await first.WriteAsync(new short[320], cancellationToken).ConfigureAwait(false);
            if (first.QueuedSamples != 0 || owner.CallbackCount != 0)
                throw new InvalidOperationException("Paused playback retained PCM or restarted the audio unit.");
            await owner.ResumeListeningAsync(cancellationToken).ConfigureAwait(false);
            if (owner.Write(outgoingVersion, new short[320]) != 320 || owner.QueuedOutput != 0)
                throw new InvalidOperationException("Recovery replayed PCM from the retired audio generation.");
            await ExerciseAsync(first, cancellationToken).ConfigureAwait(false);
        }
        finally { await first.DisposeAsync().ConfigureAwait(false); }
        await using IAudioPlayback replacement = backend.OpenPlayback(route, PcmAudioFormat.Voice8KhzMono16Bit);
        // A repeated disposal of the retired endpoint must not affect its replacement.
        await first.DisposeAsync().ConfigureAwait(false);
        ((IImmediateAudioStop)first).StopImmediately();
        if (((IAudioPlaybackCallbackDiagnostics)first).OutputCallbackCount != 0)
            throw new InvalidOperationException("A retired endpoint reported its replacement's callbacks.");
        await ExerciseAsync(replacement, cancellationToken).ConfigureAwait(false);
        AudioDeviceInfo input = backend.EnumerateDevices(AudioDirection.Input).Single();
        await using (IAudioCapture capture = backend.OpenCapture(input, PcmAudioFormat.Voice8KhzMono16Bit))
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try
            {
                await capture.StartAsync(canceled.Token).ConfigureAwait(false);
                throw new InvalidOperationException("Canceled capture startup was admitted.");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            if (capture.IsRunning) throw new InvalidOperationException("Canceled capture is running.");
        }
        await using (IAudioCapture idleCapture = backend.OpenCapture(input, PcmAudioFormat.Voice8KhzMono16Bit))
            ((IImmediateAudioStop)idleCapture).StopImmediately();
        await ExerciseAsync(replacement, cancellationToken).ConfigureAwait(false);
        await ExerciseCaptureSignalAsync(cancellationToken).ConfigureAwait(false);
        return "PASS\nRemoteIO callbacks, stereo/mono conversion, drain, immediate stop, interruption recovery fencing, resume, replacement, native starvation, idle-tail exclusion, and cancellation-woken capture signal with retained native lifetime.";
    }

    private static async Task ExerciseCaptureSignalAsync(CancellationToken token)
    {
        // Test the capture wait's cancellation and handle lease using an output-only,
        // unstarted device. This does not enable or request microphone capture.
        using var device = RemoteIoDevice.Create(48_000, input: false);
        using var cancellation = new CancellationTokenSource();
        using var signal = new RemoteIoDevice.CaptureSignal(device, cancellation.Token);
        device.Dispose();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> wait = Task.Run(() => { entered.TrySetResult(); return signal.Wait(); }, token);
        await entered.Task.WaitAsync(token).ConfigureAwait(false);
        cancellation.Cancel();
        if (!await wait.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false))
            throw new InvalidOperationException("Capture cancellation did not wake the retained native wait.");
    }

    /// <summary>Requires a simulator permission choice in advance; never starts microphone capture.</summary>
    public static async Task<string> RunInputSelectionAsync(CancellationToken cancellationToken = default)
    {
        using var owner = new IosAudioSessionOwner();
        if (owner.MicrophonePermissionGranted is null)
            throw new InvalidOperationException("Set simulator microphone permission to granted or denied before this check.");
        using var backend = new IosAudioBackend(owner);
        await using var playback = backend.OpenPlayback(backend.EnumerateDevices(AudioDirection.Output).Single(),
            PcmAudioFormat.Voice8KhzMono16Bit);
        await ExerciseAsync(playback, cancellationToken).ConfigureAwait(false);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await owner.PrepareInputSelectionAsync(cancelled.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (owner.State != IosAudioExecutionState.Listening)
                throw new InvalidOperationException("Cancelled input discovery disturbed listening.");
        }
        if (owner.MicrophonePermissionGranted == false)
        {
            try { await owner.PrepareInputSelectionAsync(cancellationToken).ConfigureAwait(false); }
            catch (UnauthorizedAccessException)
            {
                await ExerciseAsync(playback, cancellationToken).ConfigureAwait(false);
                return "PASS\nDenied microphone selection and cancelled discovery preserve listening. No capture or network activity.";
            }
            throw new InvalidOperationException("Denied input discovery was admitted.");
        }
        var inputs = await owner.PrepareInputSelectionAsync(cancellationToken).ConfigureAwait(false);
        if (owner.State != IosAudioExecutionState.RequiresResume || owner.CallbackCount != 0)
            throw new InvalidOperationException("Input discovery left processing active.");
        bool rejected = false;
        try { owner.SelectInput("qualification-unavailable-input"); }
        catch (ArgumentException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("An unavailable microphone was selected.");
        if (inputs.Count > 0) owner.SelectInput(inputs[0].Id);
        owner.SelectInput(null);
        owner.RestoreInputPreference("qualification-saved-unavailable");
        if (owner.PreferredInputId != "qualification-saved-unavailable")
            throw new InvalidOperationException("Restoring an unavailable saved input silently changed its preference.");
        rejected = false;
        try { owner.SelectInput(null); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("A stale input chooser changed the route preference.");
        owner.RestoreInputPreference(null);
        await owner.ResumeListeningAsync(cancellationToken).ConfigureAwait(false);
        await ExerciseAsync(playback, cancellationToken).ConfigureAwait(false);
        return $"PASS\nInput discovery ({inputs.Count} routes), unavailable/stale selection rejection, system default, explicit resume and output recovery. No microphone capture or network activity.";
    }

    private static async Task ExerciseInterruptionAsync(IosAudioSessionOwner owner, IAudioPlayback playback, CancellationToken token)
    {
        int? recovery = null;
        void Changed(object? sender, IosAudioSessionChange change) => recovery = change.RecoveryGeneration;
        owner.Changed += Changed;
        try
        {
            owner.HandleInterruption(began: true, shouldResume: false);
            owner.HandleInterruption(began: false, shouldResume: false);
            if (recovery.HasValue || owner.State != IosAudioExecutionState.Interrupted)
                throw new InvalidOperationException("Interruption resumed without system permission.");
            owner.HandleInterruption(began: false, shouldResume: true);
            int generation = recovery ?? throw new InvalidOperationException("Interruption did not request recovery.");
            await owner.ResumeInterruptedListeningAsync(generation, token).ConfigureAwait(false);
            await ExerciseAsync(playback, token).ConfigureAwait(false);
            await RejectStaleRecoveryAsync(owner, generation, token).ConfigureAwait(false);
            owner.HandleInterruption(began: true, shouldResume: false);
            owner.HandleInterruption(began: false, shouldResume: true);
            generation = recovery ?? throw new InvalidOperationException("Second interruption did not request recovery.");
            owner.StopImmediately();
            await RejectStaleRecoveryAsync(owner, generation, token).ConfigureAwait(false);
            // A fresh interruption after an explicit stop must not downgrade the
            // stronger resume requirement and admit its later automatic recovery.
            owner.HandleInterruption(began: true, shouldResume: false);
            owner.HandleInterruption(began: false, shouldResume: true);
            if (recovery.HasValue || owner.State != IosAudioExecutionState.RequiresResume)
                throw new InvalidOperationException("A stale interruption bypassed explicit resume.");
            await owner.ResumeListeningAsync(token).ConfigureAwait(false);
            await ExerciseAsync(playback, token).ConfigureAwait(false);
        }
        finally { owner.Changed -= Changed; }
    }

    private static async Task RejectStaleRecoveryAsync(IosAudioSessionOwner owner, int generation, CancellationToken token)
    {
        try { await owner.ResumeInterruptedListeningAsync(generation, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Stale or duplicate interruption recovery was admitted.");
    }

    private static async Task ExerciseContinuityAsync(IAudioPlayback playback, CancellationToken token)
    {
        var continuity = (IAudioPlaybackContinuityDiagnostics)playback;
        // A deliberate gap confirms native counters are wired through the
        // existing mixer's continuity contract; the PCM itself remains silent.
        await Task.Delay(100, token).ConfigureAwait(false);
        if (continuity.PendingStarvedDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("Native playback starvation was not observed.");
        TimeSpan before = continuity.StarvedDuration;
        await playback.WriteAsync(new short[320], token).ConfigureAwait(false);
        if (continuity.StarvedDuration <= before)
            throw new InvalidOperationException("Resumed playback did not commit the observed gap.");
        await playback.DrainAsync(token).ConfigureAwait(false);
        continuity.EndExpectedPlayback();
        await Task.Delay(100, token).ConfigureAwait(false);
        if (continuity.PendingStarvedDuration != TimeSpan.Zero)
            throw new InvalidOperationException("Idle callbacks were counted as active playback starvation.");
    }

    private static async Task ExerciseAsync(IAudioPlayback playback, CancellationToken cancellationToken)
    {
        await playback.WriteAsync(new short[800 * playback.Format.Channels], cancellationToken).ConfigureAwait(false);
        await playback.FlushAsync(cancellationToken).ConfigureAwait(false);
        await playback.DrainAsync(cancellationToken).ConfigureAwait(false);
        if (playback.QueuedSamples != 0 || ((IAudioPlaybackCallbackDiagnostics)playback).OutputCallbackCount <= 0)
            throw new IOException("The iOS audio unit did not present and drain its silent test PCM.");
    }
}
