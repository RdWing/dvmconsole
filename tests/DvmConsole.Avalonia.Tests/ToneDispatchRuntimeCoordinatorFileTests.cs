// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Services;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class ToneDispatchRuntimeCoordinatorFileTests
    {
        [Fact]
        public async Task SendWaveFilePcm_ReadsOnlyValidatedDataChunkAndRejectsInvalidFormat()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-gate55-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                byte[] pcm = new byte[320];
                for (var i = 0; i < pcm.Length; i++)
                    pcm[i] = (byte)(i % 251);
                File.WriteAllBytes(path, MakeWave(pcm, includeJunkChunk: true));

                byte[]? sent = null;
                var target = new DvmConsole.Platform.Audio.TransmitTarget(
                    "SYS",
                    "101",
                    1,
                    DvmConsole.Platform.Audio.VoiceMode.Dmr,
                    1001);
                await using var coordinator = new ToneDispatchRuntimeCoordinator(
                    () => new[] { target },
                    _ => true,
                    () => false,
                    (_, payload, _, _) =>
                    {
                        sent = payload.ToArray();
                        return Task.CompletedTask;
                    });

                var result = await coordinator.ReadAndSendWaveFileAsync(
                    path,
                    sendStartSignal: false,
                    CancellationToken.None);

                Assert.True(result);
                Assert.Equal(dvmconsole.TonePcmNormalizer.NormalizeAlertTonePcm(pcm), sent);

                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                Assert.False(await coordinator.ReadAndSendWaveFileAsync(
                    path,
                    sendStartSignal: false,
                    CancellationToken.None));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public async Task PreviewGeneratedPcm_UsesLocalPreviewOnlyAndHonorsCancellation()
        {
            var previewed = new List<byte[]>();
            var sends = 0;
            await using var coordinator = new ToneDispatchRuntimeCoordinator(
                () => Array.Empty<DvmConsole.Platform.Audio.TransmitTarget>(),
                _ => true,
                () => false,
                (_, _, _, _) =>
                {
                    sends++;
                    return Task.CompletedTask;
                },
                previewPcm: (pcm, _) =>
                {
                    previewed.Add(pcm.ToArray());
                    return Task.CompletedTask;
                });

            byte[] pcm = new byte[320];
            await coordinator.PreviewGeneratedPcmAsync(pcm, CancellationToken.None);

            Assert.Equal(pcm, Assert.Single(previewed));
            Assert.Equal(0, sends);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await coordinator.PreviewGeneratedPcmAsync(pcm, cancellation.Token);
            Assert.Single(previewed);
        }

        private static byte[] MakeWave(byte[] pcm, bool includeJunkChunk)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            int extra = includeJunkChunk ? 12 : 0;
            writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            writer.Write(36 + extra + pcm.Length);
            writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            if (includeJunkChunk)
            {
                writer.Write(new[] { (byte)'J', (byte)'U', (byte)'N', (byte)'K' });
                writer.Write(4);
                writer.Write(0x10203040);
            }
            writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(16000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            writer.Write(pcm.Length);
            writer.Write(pcm);
            return stream.ToArray();
        }
    }
}
