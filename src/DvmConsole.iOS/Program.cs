// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using UIKit;

namespace DvmConsole.iOS;

internal static class Program
{
    internal static bool VocoderSmokeRequested { get; private set; }

    private static void Main(string[] args)
    {
        // The sandboxed host uses the bundled managed Opus codec. Do not probe
        // for development-machine libopus libraries through dynamic discovery.
        Concentus.OpusCodecFactory.AttemptToUseNativeLibrary = false;
        VocoderSmokeRequested = args.Contains("--vocoder-smoke", StringComparer.Ordinal);
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
