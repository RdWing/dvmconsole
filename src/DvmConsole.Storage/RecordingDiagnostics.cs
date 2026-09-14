// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;

namespace DvmConsole.Storage;

/// <summary>Synthetic TAR qualification through the same store used by the desktop.</summary>
public static class RecordingDiagnostics
{
    public static async Task<string> RunAsync(string temporaryParent, CancellationToken cancellationToken = default)
    {
        string root = Path.Combine(temporaryParent, "recording-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RecordingId id;
            var completed = new TaskCompletionSource<RecordingFinalizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var store = new OpusRecordingStore(root, (_, error) => completed.TrySetException(error), 0))
            {
                store.RecordingFinalized += (_, result) => completed.TrySetResult(result);
                var definition = new ChannelRuntimeDefinition("Synthetic", "Diagnostic", "p25", 1, 0);
                var channel = ConsoleChannelState.GetId(definition);
                await using var capture = new CallRecordingManager(store, retentionDays: 0,
                    resolveSubscriberAlias: (_, _) => "Synthetic");
                var descriptor = new ChannelRecordingDescriptor(channel, definition, true, false);
                short[] samples = Enumerable.Range(0, 8000)
                    .Select(index => (short)(6000 * Math.Sin(2 * Math.PI * 440 * index / 8000))).ToArray();
                capture.WriteEpisodeSamples(descriptor, 1, 1, 1, samples.AsMemory(0, 4000), 1);
                await capture.CheckpointAsync(cancellationToken).ConfigureAwait(false);
                string activePath = store.ActivePaths.Single();
                using (var checkpoint = new FileStream(activePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] header = new byte[44];
                    checkpoint.ReadExactly(header);
                    if (checkpoint.Length != 8044 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40, 4)) != 8000)
                        throw new IOException("Active TAR checkpoint did not flush its PCM and current WAV length.");
                }
                capture.WriteEpisodeSamples(descriptor, 1, 1, 1, samples.AsMemory(4000), 1);
                capture.StopChannel(descriptor);
                await capture.DrainAcceptedWorkAsync(cancellationToken).ConfigureAwait(false);
                RecordingFinalizationResult finalized = await completed.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                if (finalized.Error is not null) throw new IOException("TAR finalization failed.", finalized.Error);
                if (!finalized.IsPlayable || finalized.Metadata?.SubscriberAlias != "Synthetic")
                    throw new IOException("TAR finalization did not retain playable media and metadata.");
                id = new RecordingId(Guid.Parse(finalized.Metadata.RecordingId));
            }
            // Reopening exercises the persisted catalog, rather than an in-memory result.
            await using (var reopened = new OpusRecordingStore(root, null, 0))
            {
                var catalog = new List<RecordingDescriptor>();
                await foreach (RecordingDescriptor item in reopened.ListAsync(cancellationToken)) catalog.Add(item);
                if (catalog.Count != 1 || catalog[0].Id != id || !catalog[0].IsFinalized)
                    throw new IOException("The reopened TAR catalog did not retain the recording.");
                await using Stream media = await reopened.OpenReadAsync(id, cancellationToken).ConfigureAwait(false);
                await using IAudioPcmStreamReader decoder = await PcmStreamDecoder.OpenAsync(media, cancellationToken).ConfigureAwait(false);
                short[] buffer = new short[4096];
                int total = 0;
                bool audible = false;
                int count;
                while ((count = await decoder.ReadSamplesAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    audible |= buffer.AsSpan(0, count).ContainsAnyExcept((short)0);
                    if (total > decoder.SampleRate * 2) throw new IOException("Decoded TAR duration exceeded the fixture.");
                }
                if (!audible || total < decoder.SampleRate / 2)
                    throw new IOException("The finalized Opus recording did not decode its synthetic PCM.");
            }
            return "PASS\nSession capture queue, active TAR checkpoint and continued capture, Opus finalization, metadata, catalog reopen, and decoded playback.";
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
