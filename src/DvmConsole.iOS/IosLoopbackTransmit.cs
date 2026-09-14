// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Threading.Channels;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.iOS;

/// <summary>Exercises production call framing and pacing against an owned loopback receiver.</summary>
internal static class IosLoopbackTransmit
{
    // Fixed synthetic qualification material; never loaded from an operator configuration.
    internal static ReadOnlyMemory<byte> P25TestKey { get; } = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    public static async Task SendAsync(FneLoopbackMaster master, FneTrafficProtocol protocol, bool secure = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var packets = Channel.CreateBounded<(byte[] Payload, ushort Sequence, uint Stream)>(64);
        void Send(ReadOnlyMemory<byte> payload, ushort sequence, uint stream)
        {
            if (!packets.Writer.TryWrite((payload.ToArray(), sequence, stream)))
                throw new InvalidOperationException("Qualification packet queue overflowed.");
        }
        async Task ForwardAsync()
        {
            await foreach (var packet in packets.Reader.ReadAllAsync(deadline.Token).ConfigureAwait(false))
                await master.SendTrafficAsync(protocol, packet.Payload, packet.Sequence, packet.Stream,
                    deadline.Token).ConfigureAwait(false);
        }
        Task forwarding = ForwardAsync();
        try
        {
            using var backend = new SoftwareVocoderBackend(linkage: NativeVocoderLinkage.StaticallyLinked);
            short[] pcm = new short[8_640]; // 1.08 seconds: whole P25 LDUs, with a padded NXDN tail.
            for (int sample = 0; sample < pcm.Length; sample++)
                pcm[sample] = (short)(6000 * Math.Sin(2 * Math.PI * 440 * sample / 8000));
            if (protocol == FneTrafficProtocol.P25)
            {
                using var call = new P25TxCallSession(2, 100, 99, backend.CreateSession(VocoderMode.P25Imbe), Send,
                    secure ? new P25TxEncryptionOptions(0x84, 1, P25TestKey, new byte[9]) : null);
                call.Start();
                call.Process(pcm);
                await call.EndAsync(deadline.Token).ConfigureAwait(false);
            }
            else if (protocol == FneTrafficProtocol.Nxdn)
            {
                using var call = new NxdnTxCallSession(2, 100, true, 99, backend.CreateSession(VocoderMode.NxdnAmbe), Send);
                call.Start();
                call.Process(pcm);
                await call.EndAsync(deadline.Token).ConfigureAwait(false);
            }
            else throw new ArgumentOutOfRangeException(nameof(protocol));
        }
        finally
        {
            packets.Writer.TryComplete();
            await forwarding.ConfigureAwait(false);
        }
    }
}
