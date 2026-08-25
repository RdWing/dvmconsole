namespace DvmConsole.Desktop;

// Applies the fallible runtime portion of an audio-settings change. Persistent
// settings are committed by the caller only after this transaction succeeds.
internal sealed class AudioRuntimeSettingsTransaction
{
    private readonly Func<bool, Task> setWarmMicrophone;
    private readonly Func<ApplicationAudioConfiguration, Task> reconfigureAudio;

    public AudioRuntimeSettingsTransaction(
        Func<bool, Task> setWarmMicrophone,
        Func<ApplicationAudioConfiguration, Task> reconfigureAudio)
    {
        this.setWarmMicrophone = setWarmMicrophone ??
            throw new ArgumentNullException(nameof(setWarmMicrophone));
        this.reconfigureAudio = reconfigureAudio ??
            throw new ArgumentNullException(nameof(reconfigureAudio));
    }

    public async Task ApplyAsync(
        bool reconfigureRoute,
        ApplicationAudioConfiguration previousConfiguration,
        ApplicationAudioConfiguration proposedConfiguration,
        bool restoreWarmMicrophone)
    {
        if (!reconfigureRoute && !restoreWarmMicrophone)
            return;

        bool warmMicrophoneDisabled = false;
        try
        {
            if (restoreWarmMicrophone)
            {
                await setWarmMicrophone(false).ConfigureAwait(false);
                warmMicrophoneDisabled = true;
            }
            if (reconfigureRoute)
                await reconfigureAudio(proposedConfiguration).ConfigureAwait(false);
            if (restoreWarmMicrophone)
            {
                await setWarmMicrophone(true).ConfigureAwait(false);
                warmMicrophoneDisabled = false;
            }
        }
        catch (Exception applicationException)
        {
            var rollback = new AsyncCleanup();
            if (reconfigureRoute)
            {
                await rollback.RunTaskAsync(() =>
                    reconfigureAudio(previousConfiguration)).ConfigureAwait(false);
            }
            if (restoreWarmMicrophone && warmMicrophoneDisabled)
            {
                await rollback.RunTaskAsync(() =>
                    setWarmMicrophone(true)).ConfigureAwait(false);
            }

            try
            {
                rollback.ThrowIfFailed();
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Audio settings application and rollback both failed.",
                    applicationException,
                    rollbackException);
            }

            throw;
        }
    }
}
