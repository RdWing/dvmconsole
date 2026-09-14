// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace DvmConsole.Vocoder;

/// <summary>Offline codec checks for host qualification. Never opens audio or radio endpoints.</summary>
public static class VocoderDiagnostics
{
    public static IReadOnlyList<string> Run(NativeVocoderLinkage linkage)
    {
        var results = new List<string>();
        using var backend = new SoftwareVocoderBackend(linkage: linkage);
        short[] input = Enumerable.Range(0, 160).Select(index => (short)((index * 317 % 20000) - 10000)).ToArray();
        foreach (VocoderMode mode in Enum.GetValues<VocoderMode>())
        {
            using IVocoderSession session = backend.CreateSession(mode);
            var processing = (IReceiveAudioProcessingSession)session;
            processing.DeferReceiveAudioProcessing();
            byte[] codeword = new byte[VocoderFrameSizes.CodewordBytes(mode)];
            short[] decoded = new short[160];
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int frame = 0; frame < 30; frame++)
            {
                if (frame == 15) session.Reset();
                Require(session.Encode(input, codeword) == codeword.Length, mode, "encode");
                digest.AppendData(codeword);
                Require((frame % 7 == 6 ? session.DecodeLost(decoded) : session.Decode(codeword, decoded)) == 0, mode, "decode");
                digest.AppendData(MemoryMarshal.AsBytes(decoded.AsSpan()));
                processing.ProcessReceiveAudio(decoded);
                digest.AppendData(MemoryMarshal.AsBytes(decoded.AsSpan()));
            }
            Require(session.FlushEncode(codeword) == codeword.Length && session.FlushEncode(codeword) == 0, mode, "flush");
            if (mode == VocoderMode.P25Imbe)
            {
                var tone = (IP25GeneratedToneVocoderSession)session;
                Require(tone.EncodeSingleTone(1000, codeword) == codeword.Length &&
                    Convert.ToHexString(codeword) == "09230B0DC4A5CAE8280A32", mode, "independent P25 tone vector");
            }
            else
            {
                using var half = (IHalfRateVocoderSession)backend.CreateSession(mode);
                byte[] parameters = new byte[7];
                byte[] recovered = new byte[7];
                Require(half.EncodeParameters(input, parameters) == 7, mode, "encode parameters");
                half.BuildCodeword(parameters, codeword);
                Require(half.ExtractParameters(codeword, recovered) == 0 && parameters.AsSpan().SequenceEqual(recovered), mode, "parameter round trip");
                Require(half.DecodeParameters(recovered, decoded) == 0, mode, "decode parameters");
                Require(half.DecodeParameters(recovered, decoded, correctedErrors: 15, lost: true) == 0, mode, "lost parameters");
                Require(half.FlushEncodeParameters(parameters) == 7 && half.FlushEncodeParameters(parameters) == 0, mode, "parameter flush");
                digest.AppendData(codeword);
                digest.AppendData(MemoryMarshal.AsBytes(decoded.AsSpan()));
            }
            results.Add($"{mode}: {Convert.ToHexString(digest.GetHashAndReset())}");
        }
        return results;
    }

    private static void Require(bool passed, VocoderMode mode, string operation)
    {
        if (!passed) throw new InvalidOperationException($"Vocoder qualification failed: {mode}, {operation}.");
    }
}
