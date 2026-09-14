// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.iOS;

/// <summary>Explicit simulator qualification runs after UIKit and the app shell are ready.</summary>
internal static class IosDiagnostics
{
    private static readonly string[] RequestVariables =
        ["DVM_LAYOUT_SMOKE", "DVM_FONT_SMOKE", "DVM_PERFORMANCE_SMOKE", "DVM_BACKGROUND_SMOKE", "DVM_VOCODER_SMOKE", "DVM_AUDIO_SMOKE", "DVM_RECORDING_SMOKE", "DVM_SESSION_SMOKE", "DVM_STUDIO_SMOKE", "DVM_CONNECTION_SMOKE", "DVM_NETWORK_SMOKE", "DVM_TRANSMIT_SMOKE", "DVM_ROUTES_SMOKE", "DVM_HELP_SMOKE", "DVM_INPUTS_SMOKE", "DVM_ACCESSIBILITY_SMOKE"];

    public static bool IsRequested => Program.VocoderSmokeRequested ||
        RequestVariables.Any(name => Environment.GetEnvironmentVariable(name) == "1");

    public static async Task RunRequestedAsync()
    {
        if (Environment.GetEnvironmentVariable("DVM_LAYOUT_SMOKE") == "1")
            await RunAsync("layout", IosLayoutPreferencesDiagnostics.RunAsync);
        if (Environment.GetEnvironmentVariable("DVM_FONT_SMOKE") == "1")
            await RunAsync("font", () => Task.FromResult(IosFontDiagnostics.Run()));
        if (Environment.GetEnvironmentVariable("DVM_PERFORMANCE_SMOKE") == "1")
            await RunAsync("performance", IosReceiveSessionDiagnostics.RunPerformanceAsync);
        if (Environment.GetEnvironmentVariable("DVM_BACKGROUND_SMOKE") == "1")
            await RunAsync("background", IosReceiveSessionDiagnostics.RunBackgroundAsync);
        if (Environment.GetEnvironmentVariable("DVM_ACCESSIBILITY_SMOKE") == "1")
            await RunAsync("accessibility", IosAccessibilityDiagnostics.RunAsync);
        if (Program.VocoderSmokeRequested || Environment.GetEnvironmentVariable("DVM_VOCODER_SMOKE") == "1")
            await RunAsync("vocoder", () => Task.FromResult("PASS\n" + string.Join("\n",
                DvmConsole.Vocoder.VocoderDiagnostics.Run(DvmConsole.Vocoder.NativeVocoderLinkage.StaticallyLinked))));
        if (Environment.GetEnvironmentVariable("DVM_TRANSMIT_SMOKE") == "1")
            await RunAsync("transmit", () => DvmConsole.Application.TransmitRuntimeDiagnostics.RunAsync(
                () => new DvmConsole.Vocoder.SoftwareVocoderBackend(linkage: DvmConsole.Vocoder.NativeVocoderLinkage.StaticallyLinked)));
        if (Environment.GetEnvironmentVariable("DVM_AUDIO_SMOKE") == "1")
            await RunAsync("audio", () => DvmConsole.Audio.IosAudioDiagnostics.RunPlaybackAsync());
        if (Environment.GetEnvironmentVariable("DVM_INPUTS_SMOKE") == "1")
            await RunAsync("inputs", IosInputPreferencesDiagnostics.RunAsync);
        if (Environment.GetEnvironmentVariable("DVM_ROUTES_SMOKE") == "1")
            await RunAsync("routes", IosAudioRouteDiagnostics.RunAsync);
        if (Environment.GetEnvironmentVariable("DVM_HELP_SMOKE") == "1")
            await RunAsync("help", IosHelpDiagnostics.RunAsync);
        if (Environment.GetEnvironmentVariable("DVM_RECORDING_SMOKE") == "1")
            await RunAsync("recording", () => DvmConsole.Storage.RecordingDiagnostics.RunAsync(Path.GetTempPath()));
        if (Environment.GetEnvironmentVariable("DVM_SESSION_SMOKE") == "1")
            await RunAsync("session", () => IosReceiveSessionDiagnostics.RunAsync());
        if (Environment.GetEnvironmentVariable("DVM_STUDIO_SMOKE") == "1")
            await RunAsync("studio", IosStudioDiagnostics.RunAsync);
        if (Environment.GetEnvironmentVariable("DVM_CONNECTION_SMOKE") == "1")
            await RunAsync("connection", () => DvmConsole.FneClient.FneLoopbackDiagnostics.RunAsync());
        if (Environment.GetEnvironmentVariable("DVM_NETWORK_SMOKE") == "1")
            await RunAsync("network", () => IosReceiveSessionDiagnostics.RunAsync(useLoopback: true));
    }

    private static async Task RunAsync(string name, Func<Task<string>> check)
    {
        string result;
        try { result = await check().ConfigureAwait(false); }
        catch (Exception exception) { result = "FAIL\n" + exception; }
        try
        {
            await File.WriteAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                name + "-smoke.txt"), result).ConfigureAwait(false);
        }
        catch (Exception exception) { Console.Error.WriteLine($"Cannot write {name} qualification report: {exception}"); }
    }
}
