// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

public interface IConsoleReceiveProcessingSettings
{
    ImmutableDictionary<VocoderMode, ReceiveAudioProcessingOptions> ReceiveProcessing { get; }
    bool CanSaveReceiveProcessing { get; }
    ValueTask SetReceiveProcessingAsync(VocoderMode mode, ReceiveAudioProcessingOptions options, CancellationToken cancellationToken = default);
}

public sealed partial class ConsoleReceiveSession : IConsoleReceiveProcessingSettings
{
    private ImmutableDictionary<VocoderMode, ReceiveAudioProcessingOptions> receiveProcessing = ConsoleReceiveProcessingProfile.Defaults;
    public ImmutableDictionary<VocoderMode, ReceiveAudioProcessingOptions> ReceiveProcessing => Volatile.Read(ref receiveProcessing);
    public bool CanSaveReceiveProcessing => dependencies.Preferences is IConsoleReceiveProcessingStore;

    public ValueTask SetReceiveProcessingAsync(VocoderMode mode, ReceiveAudioProcessingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = ConsoleReceiveProcessingProfile.Normalize(options);
        if (!ConsoleReceiveProcessingProfile.Defaults.ContainsKey(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return RunCommandAsync(async token =>
        {
            var store = dependencies.Preferences as IConsoleReceiveProcessingStore
                ?? throw new NotSupportedException("Receive processing settings are unavailable.");
            await store.SaveReceiveProcessingAsync(mode, options, token).ConfigureAwait(false);
            Volatile.Write(ref receiveProcessing, ReceiveProcessing.SetItem(mode, options));
            try
            {
                // Recovery expands the request to every decoder sharing its physical
                // output. It preserves listening/TAR intent and the current mute policy.
                await receive.RecoverActiveOutputsAsync(() => state.Execution.Snapshot.CanReceive, token).ConfigureAwait(false);
                SetStatus(state.Execution.Snapshot.CanReceive
                    ? "Receive processing saved and applied."
                    : "Receive processing saved. It will apply when listening resumes.");
            }
            catch (Exception exception)
            {
                SetStatus($"Receive processing saved, but audio could not restart: {exception.Message}");
                throw;
            }
        }, cancellationToken);
    }
}
