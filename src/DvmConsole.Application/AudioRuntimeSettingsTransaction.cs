using System.Runtime.ExceptionServices;

namespace DvmConsole.Application;

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
            var rollbackFailures = new List<Exception>();
            if (reconfigureRoute)
            {
                try
                {
                    await reconfigureAudio(previousConfiguration).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    rollbackFailures.Add(exception);
                }
            }
            if (restoreWarmMicrophone && warmMicrophoneDisabled)
            {
                try
                {
                    await setWarmMicrophone(true).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    rollbackFailures.Add(exception);
                }
            }

            try
            {
                if (rollbackFailures.Count == 1)
                    ExceptionDispatchInfo.Capture(rollbackFailures[0]).Throw();
                if (rollbackFailures.Count > 1)
                    throw new AggregateException("One or more audio rollback operations failed.", rollbackFailures);
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
