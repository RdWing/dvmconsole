// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Owns the shell-side lifecycle for generated tone/DTMF dispatch.
    /// Target resolution happens once per request; the audio router owns
    /// protocol fan-out and end-of-call signaling.
    /// </summary>
    public sealed class ToneDispatchRuntimeCoordinator : IAsyncDisposable
    {
        private readonly Func<IReadOnlyList<TransmitTarget>> resolveTargets;
        private readonly Func<IReadOnlyList<TransmitTarget>, bool> areTargetsAvailable;
        private readonly Func<bool> isMicPttActive;
        private readonly Func<
            IReadOnlyList<TransmitTarget>,
            ReadOnlyMemory<byte>,
            bool,
            CancellationToken,
            Task> transmitPcm;
        private readonly Action<string>? reportStatus;
        private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task>? previewPcm;
        private readonly SemaphoreSlim dispatchGate = new(1, 1);
        private readonly object stateGate = new();
        private CancellationTokenSource? activeCancellation;
        private bool disposed;

        public ToneDispatchRuntimeCoordinator(
            Func<IReadOnlyList<TransmitTarget>> resolveTargets,
            Func<IReadOnlyList<TransmitTarget>, bool> areTargetsAvailable,
            Func<bool> isMicPttActive,
            Func<
                IReadOnlyList<TransmitTarget>,
                ReadOnlyMemory<byte>,
                bool,
                CancellationToken,
                Task> transmitPcm,
            Action<string>? reportStatus = null,
            Func<ReadOnlyMemory<byte>, CancellationToken, Task>? previewPcm = null)
        {
            this.resolveTargets = resolveTargets ?? throw new ArgumentNullException(nameof(resolveTargets));
            this.areTargetsAvailable = areTargetsAvailable
                ?? throw new ArgumentNullException(nameof(areTargetsAvailable));
            this.isMicPttActive = isMicPttActive
                ?? throw new ArgumentNullException(nameof(isMicPttActive));
            this.transmitPcm = transmitPcm ?? throw new ArgumentNullException(nameof(transmitPcm));
            this.reportStatus = reportStatus;
            this.previewPcm = previewPcm;
        }

        public async Task<bool> SendGeneratedPcmAsync(
            ReadOnlyMemory<byte> pcm,
            bool sendStartSignal,
            CancellationToken cancellationToken,
            IReadOnlyList<TransmitTarget>? targetSnapshot = null)
        {
            if (pcm.Length == 0)
            {
                return false;
            }

            lock (stateGate)
            {
                if (disposed)
                {
                    return false;
                }
            }

            var targets = (targetSnapshot ?? resolveTargets() ?? Array.Empty<TransmitTarget>()).ToArray();
            if (targets.Length == 0)
            {
                reportStatus?.Invoke("Tone dispatch blocked: no transmit target is selected.");
                return false;
            }

            if (!areTargetsAvailable(targets))
            {
                reportStatus?.Invoke("Tone dispatch blocked: selected target is unavailable.");
                return false;
            }

            if (isMicPttActive())
            {
                reportStatus?.Invoke("Tone dispatch blocked: microphone PTT is active.");
                return false;
            }

            try
            {
                await dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            CancellationTokenSource? linkedCancellation = null;
            try
            {
                lock (stateGate)
                {
                    if (disposed)
                    {
                        return false;
                    }

                    linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    activeCancellation = linkedCancellation;
                }

                byte[] preparedPcm = PreparePcm(pcm);
                await transmitPcm(
                    targets,
                    preparedPcm,
                    sendStartSignal,
                    linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                reportStatus?.Invoke("Tone dispatch cancelled.");
                return false;
            }
            catch (Exception exception)
            {
                reportStatus?.Invoke($"Tone dispatch failed: {exception.Message}");
                return false;
            }
            finally
            {
                lock (stateGate)
                {
                    if (ReferenceEquals(activeCancellation, linkedCancellation))
                    {
                        activeCancellation = null;
                    }
                }

                linkedCancellation?.Dispose();
                dispatchGate.Release();
            }

            return true;
        }

        public async Task PreviewGeneratedPcmAsync(
            ReadOnlyMemory<byte> pcm,
            CancellationToken cancellationToken)
        {
            if (pcm.Length == 0 || previewPcm is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await previewPcm(PreparePcm(pcm), cancellationToken).WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                reportStatus?.Invoke("Tone preview cancelled.");
            }
            catch (Exception exception)
            {
                reportStatus?.Invoke($"Tone preview failed: {exception.Message}");
            }
        }

        public async Task<bool> ReadAndSendWaveFileAsync(
            string path,
            bool sendStartSignal,
            CancellationToken cancellationToken,
            IReadOnlyList<TransmitTarget>? targetSnapshot = null)
        {
            byte[] pcm;
            try
            {
                pcm = await ReadWavePcmAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                reportStatus?.Invoke("Tone file dispatch cancelled.");
                return false;
            }
            catch (Exception exception)
            {
                reportStatus?.Invoke($"Tone file dispatch failed: {exception.Message}");
                return false;
            }

            return await SendGeneratedPcmAsync(
                    pcm,
                    sendStartSignal,
                    cancellationToken,
                    targetSnapshot)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            CancellationTokenSource? cancellation;
            lock (stateGate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                cancellation = activeCancellation;
            }

            cancellation?.Cancel();
            await dispatchGate.WaitAsync().ConfigureAwait(false);
            dispatchGate.Release();
        }

        private static async Task<byte[]> ReadWavePcmAsync(
            string path,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("A WAVE file path is required.");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                AudioPcm.BlockBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < 12)
            {
                throw new InvalidDataException("File is not a RIFF/WAVE file.");
            }

            byte[] header = await ReadExactAsync(stream, 12, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                throw new InvalidDataException("File is not a RIFF/WAVE file.");
            }

            uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            long riffEnd = 8L + riffSize;
            if (riffEnd > stream.Length || riffEnd < 12)
            {
                throw new InvalidDataException("RIFF chunk is truncated.");
            }

            bool formatSeen = false;
            byte[]? data = null;
            while (stream.Position < riffEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (riffEnd - stream.Position < 8)
                {
                    throw new InvalidDataException("Truncated chunk header.");
                }

                byte[] chunkHeader = await ReadExactAsync(stream, 8, cancellationToken)
                    .ConfigureAwait(false);
                string chunkId = System.Text.Encoding.ASCII.GetString(chunkHeader, 0, 4);
                uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
                long payloadEnd = stream.Position + chunkSize;
                if (payloadEnd > riffEnd)
                {
                    throw new InvalidDataException($"Chunk '{chunkId}' is truncated.");
                }

                if (chunkId == "fmt " && !formatSeen)
                {
                    byte[] format = await ReadExactAsync(
                        stream,
                        checked((int)Math.Min(chunkSize, 64)),
                        cancellationToken).ConfigureAwait(false);
                    if (chunkSize > format.Length)
                    {
                        stream.Position = payloadEnd;
                    }

                    ValidateFormat(format);
                    formatSeen = true;
                }
                else if (chunkId == "data" && data is null)
                {
                    if (!formatSeen)
                    {
                        throw new InvalidDataException("Data chunk appears before the fmt chunk.");
                    }

                    const uint maximumDataBytes = 32u * 1024u * 1024u;
                    if (chunkSize > maximumDataBytes || chunkSize % 2 != 0)
                    {
                        throw new InvalidDataException("WAVE PCM data is too large or not 16-bit aligned.");
                    }

                    data = await ReadExactAsync(stream, checked((int)chunkSize), cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    stream.Position = payloadEnd;
                }

                if ((chunkSize & 1) != 0)
                {
                    if (stream.Position >= riffEnd)
                    {
                        throw new InvalidDataException("Odd-sized chunk is missing its RIFF padding byte.");
                    }

                    stream.Position++;
                }
            }

            if (!formatSeen)
            {
                throw new InvalidDataException("Missing fmt chunk.");
            }

            return data ?? throw new InvalidDataException("Missing data chunk.");
        }

        private static void ValidateFormat(byte[] format)
        {
            if (format.Length < 16
                || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(0, 2)) != 1
                || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2, 2)) != 1
                || BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4, 4)) != 8000
                || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14, 2)) != 16)
            {
                throw new InvalidDataException("Only PCM mono 8 kHz 16-bit WAVE files are supported.");
            }
        }

        private static async Task<byte[]> ReadExactAsync(
            FileStream stream,
            int length,
            CancellationToken cancellationToken)
        {
            var bytes = new byte[length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                int read = await stream.ReadAsync(
                    bytes.AsMemory(offset),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidDataException("WAVE chunk is truncated.");
                }

                offset += read;
            }

            return bytes;
        }

        private static byte[] AlignToFrame(byte[] pcm)
        {
            var alignedLength = ((pcm.Length + AudioPcm.FrameBytes - 1) / AudioPcm.FrameBytes)
                * AudioPcm.FrameBytes;
            if (alignedLength == pcm.Length)
            {
                return pcm;
            }

            var aligned = new byte[alignedLength];
            Buffer.BlockCopy(pcm, 0, aligned, 0, pcm.Length);
            return aligned;
        }

        private static byte[] PreparePcm(ReadOnlyMemory<byte> pcm)
            => AlignToFrame(TonePcmNormalizer.NormalizeAlertTonePcm(pcm.ToArray()));
    }
}
